// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Text;
using LibAtrac9;
using SDL;
using static SDL.SDL3;

namespace SharpEmu.GUI;

/// <summary>
/// Loops a game's sce_sys/snd0.at9 preview music while the game is selected
/// in the library, like the console home screen. The ATRAC9 stream is decoded
/// to WAV on a background task (vendored LibAtrac9); SDL3 plays the PCM on
/// Windows, Linux and macOS.
/// </summary>
internal sealed class SndPreviewPlayer
{
    private readonly object _sync = new();
    private int _generation;
    private unsafe SDL_AudioStream* _stream;
    private int _pcmOffset;
    private int _targetQueuedBytes;
    private bool _paused;
    private bool _muted;
    private double _volume = 1.0;
    private string? _cachedPath;
    private byte[]? _cachedWav;

    public unsafe double Volume
    {
        set
        {
            lock (_sync)
            {
                _volume = double.IsFinite(value) ? Math.Clamp(value, 0.0, 1.0) : 1.0;
                if (_stream is not null)
                {
                    SDL_SetAudioStreamGain(_stream, (float)_volume);
                }
            }
        }
    }

    /// <summary>
    /// Keeps the music silent while set, even across <see cref="Play"/> and
    /// <see cref="Resume"/>; the track stays ready to continue after unmuting.
    /// </summary>
    public unsafe bool Muted
    {
        get
        {
            lock (_sync)
            {
                return _muted;
            }
        }
        set
        {
            lock (_sync)
            {
                _muted = value;
                if (value && _stream is not null)
                {
                    SDL_PauseAudioStreamDevice(_stream);
                    _paused = true;
                }
            }
        }
    }

    /// <summary>Starts looping the given snd0.at9 after a short debounce.</summary>
    public void Play(string at9Path)
    {
        int generation;
        lock (_sync)
        {
            generation = ++_generation;
        }

        _ = Task.Run(async () =>
        {
            // Debounce so skimming through the library does not decode (or
            // start) a preview per tile.
            await Task.Delay(120).ConfigureAwait(false);

            byte[]? wav;
            lock (_sync)
            {
                if (generation != _generation)
                {
                    return;
                }

                wav = string.Equals(_cachedPath, at9Path, StringComparison.OrdinalIgnoreCase)
                    ? _cachedWav
                    : null;
            }

            if (wav is null)
            {
                try
                {
                    wav = DecodeAt9ToWav(File.ReadAllBytes(at9Path));
                }
                catch (Exception)
                {
                    return; // corrupt or unsupported preview: stay silent
                }
            }

            lock (_sync)
            {
                if (generation != _generation)
                {
                    return;
                }

                _cachedPath = at9Path;
                _cachedWav = wav;
                StopLocked();
                try
                {
                    StartLocked(wav);
                }
                catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException or InvalidOperationException)
                {
                    Console.Error.WriteLine($"[GUI][WARN] Title music playback unavailable: {error.Message}");
                    StopLocked();
                }
            }
        });
    }

    public void Stop()
    {
        lock (_sync)
        {
            _generation++;
            StopLocked();
        }
    }

    /// <summary>
    /// Pauses playback while preserving the current position and queued audio.
    /// </summary>
    public unsafe void Pause()
    {
        lock (_sync)
        {
            if (_stream is null || _paused)
            {
                return;
            }
            SDL_PauseAudioStreamDevice(_stream);
            _paused = true;
        }
    }

    /// <summary>Continues the track silenced by <see cref="Pause"/>.</summary>
    public unsafe void Resume()
    {
        lock (_sync)
        {
            if (!_paused || _muted || _stream is null)
            {
                return;
            }
            SDL_ResumeAudioStreamDevice(_stream);
            _paused = false;
        }
    }

    private unsafe void StartLocked(byte[] wav)
    {
        if (wav.Length <= 44)
        {
            return;
        }

        if ((SDL_WasInit(SDL_InitFlags.SDL_INIT_AUDIO) & SDL_InitFlags.SDL_INIT_AUDIO) == 0 &&
            !SDL_InitSubSystem(SDL_InitFlags.SDL_INIT_AUDIO))
        {
            throw new InvalidOperationException("SDL audio initialization failed.");
        }

        var spec = new SDL_AudioSpec
        {
            format = SDL_AudioFormat.SDL_AUDIO_S16LE,
            channels = checked((byte)BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(22))),
            freq = BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(24)),
        };
        _stream = SDL_OpenAudioDeviceStream(SDL_AUDIO_DEVICE_DEFAULT_PLAYBACK, &spec, null, IntPtr.Zero);
        if (_stream is null)
        {
            throw new InvalidOperationException("SDL audio stream creation failed.");
        }

        SDL_SetAudioStreamGain(_stream, (float)_volume);
        _targetQueuedBytes = spec.freq * spec.channels * sizeof(short) / 4; // 250 ms of buffered audio
        _pcmOffset = 44;
        FillStreamLocked(wav);
        _paused = _muted;
        if (!_muted && !SDL_ResumeAudioStreamDevice(_stream))
        {
            throw new InvalidOperationException("SDL audio stream start failed.");
        }

        _ = PumpAsync(_generation, wav);
    }

    private async Task PumpAsync(int generation, byte[] wav)
    {
        while (true)
        {
            await Task.Delay(40).ConfigureAwait(false);
            lock (_sync)
            {
                if (generation != _generation)
                {
                    return;
                }

                FillStreamIfActiveLocked(wav);
            }
        }
    }

    private unsafe void FillStreamIfActiveLocked(byte[] wav)
    {
        if (_stream is not null && !_paused)
        {
            FillStreamLocked(wav);
        }
    }

    private unsafe void FillStreamLocked(byte[] wav)
    {
        var remaining = _targetQueuedBytes - SDL_GetAudioStreamQueued(_stream);
        while (remaining > 0)
        {
            if (_pcmOffset == wav.Length)
            {
                _pcmOffset = 44;
            }

            var bytes = Math.Min(remaining, wav.Length - _pcmOffset);
            bytes -= bytes % (BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(22)) * sizeof(short));
            if (bytes == 0)
            {
                break;
            }

            fixed (byte* data = &wav[_pcmOffset])
            {
                if (!SDL_PutAudioStreamData(_stream, (nint)data, bytes))
                {
                    break;
                }
            }

            _pcmOffset += bytes;
            remaining -= bytes;
        }
    }

    private unsafe void StopLocked()
    {
        if (_stream is not null)
        {
            SDL_DestroyAudioStream(_stream);
            _stream = null;
        }

        _paused = false;
    }

    private static readonly Guid Atrac9SubFormat = new("47E142D2-36BA-4D8D-88FC-61654F8C836C");

    /// <summary>
    /// Decodes a Sony AT9 (RIFF-wrapped ATRAC9) file to a PCM16 WAV image.
    /// Layout per Sony's container: an extensible fmt chunk whose extension
    /// carries the 4-byte codec config, a fact chunk with the sample count
    /// and encoder delay, and superframes in the data chunk.
    /// </summary>
    private static byte[] DecodeAt9ToWav(byte[] file)
    {
        using var reader = new BinaryReader(new MemoryStream(file), Encoding.ASCII);
        if (Encoding.ASCII.GetString(reader.ReadBytes(4)) != "RIFF")
        {
            throw new InvalidDataException("Not a RIFF file.");
        }

        reader.BaseStream.Position += 4; // riff size
        if (Encoding.ASCII.GetString(reader.ReadBytes(4)) != "WAVE")
        {
            throw new InvalidDataException("Not a WAVE file.");
        }

        byte[]? configData = null;
        var sampleCount = 0;
        var encoderDelay = 0;
        var dataOffset = -1;
        var dataSize = 0;

        while (reader.BaseStream.Position + 8 <= reader.BaseStream.Length)
        {
            var chunkId = Encoding.ASCII.GetString(reader.ReadBytes(4));
            var chunkSize = reader.ReadInt32();
            var chunkStart = reader.BaseStream.Position;
            switch (chunkId)
            {
                case "fmt ":
                    var formatTag = reader.ReadUInt16();
                    reader.BaseStream.Position = chunkStart + 24; // to SubFormat GUID
                    var subFormat = new Guid(reader.ReadBytes(16));
                    if (formatTag != 0xFFFE || subFormat != Atrac9SubFormat)
                    {
                        throw new InvalidDataException("Not an ATRAC9 stream.");
                    }

                    reader.BaseStream.Position += 4; // version info
                    configData = reader.ReadBytes(4);
                    break;
                case "fact":
                    sampleCount = reader.ReadInt32();
                    reader.BaseStream.Position += 4; // input overlap delay
                    encoderDelay = reader.ReadInt32();
                    break;
                case "data":
                    dataOffset = (int)chunkStart;
                    dataSize = chunkSize;
                    break;
            }

            reader.BaseStream.Position = chunkStart + chunkSize + (chunkSize & 1);
        }

        if (configData is null || sampleCount <= 0 || dataOffset < 0)
        {
            throw new InvalidDataException("Missing fmt, fact, or data chunk.");
        }

        var decoder = new Atrac9Decoder();
        decoder.Initialize(configData);
        var config = decoder.Config;

        var superframeCount = (sampleCount + encoderDelay + config.SuperframeSamples - 1) / config.SuperframeSamples;
        superframeCount = Math.Min(superframeCount, dataSize / config.SuperframeBytes);

        var channels = config.ChannelCount;
        var pcmBuffer = new short[channels][];
        for (var i = 0; i < channels; i++)
        {
            pcmBuffer[i] = new short[config.SuperframeSamples];
        }

        var wav = new byte[44 + (sampleCount * channels * 2)];
        WriteWavHeader(wav, channels, config.SampleRate, sampleCount);

        var superframe = new byte[config.SuperframeBytes];
        var decodedIndex = 0L; // per-channel, includes the encoder delay
        var written = 0;
        for (var f = 0; f < superframeCount && written < sampleCount; f++)
        {
            Buffer.BlockCopy(file, dataOffset + (f * config.SuperframeBytes), superframe, 0, config.SuperframeBytes);
            decoder.Decode(superframe, pcmBuffer);
            for (var s = 0; s < config.SuperframeSamples && written < sampleCount; s++)
            {
                if (decodedIndex++ < encoderDelay)
                {
                    continue;
                }

                var sampleOffset = 44 + ((long)written * channels * 2);
                for (var ch = 0; ch < channels; ch++)
                {
                    BinaryPrimitives.WriteInt16LittleEndian(
                        wav.AsSpan((int)(sampleOffset + (ch * 2))),
                        pcmBuffer[ch][s]);
                }

                written++;
            }
        }

        return wav;
    }

    private static void WriteWavHeader(byte[] wav, int channels, int sampleRate, int sampleCount)
    {
        var span = wav.AsSpan();
        Encoding.ASCII.GetBytes("RIFF").CopyTo(span);
        BinaryPrimitives.WriteInt32LittleEndian(span[4..], wav.Length - 8);
        Encoding.ASCII.GetBytes("WAVE").CopyTo(span[8..]);
        Encoding.ASCII.GetBytes("fmt ").CopyTo(span[12..]);
        BinaryPrimitives.WriteInt32LittleEndian(span[16..], 16);
        BinaryPrimitives.WriteInt16LittleEndian(span[20..], 1); // PCM
        BinaryPrimitives.WriteInt16LittleEndian(span[22..], (short)channels);
        BinaryPrimitives.WriteInt32LittleEndian(span[24..], sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(span[28..], sampleRate * channels * 2);
        BinaryPrimitives.WriteInt16LittleEndian(span[32..], (short)(channels * 2));
        BinaryPrimitives.WriteInt16LittleEndian(span[34..], 16); // bits per sample
        Encoding.ASCII.GetBytes("data").CopyTo(span[36..]);
        BinaryPrimitives.WriteInt32LittleEndian(span[40..], sampleCount * channels * 2);
    }
}
