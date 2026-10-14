// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Np;

public sealed record TrophyEntry(
    int Id,
    int Grade,
    string Name,
    string Description,
    bool Hidden,
    bool Unlocked,
    DateTime? UnlockedAtUtc,
    byte[]? IconPng);

public sealed record TrophyUnlockNotice(string TitleId, int TrophyId, int Grade, string Name);

public static class TrophyCatalog
{
    public static IReadOnlyList<TrophyEntry> Load(string gameDirectory, string titleId, bool includeIcons = true)
    {
        try
        {
            var packagePath = Path.Combine(gameDirectory, "sce_sys", "trophy2", "trophy00.ucp");
            if (!File.Exists(packagePath))
            {
                return [];
            }

            var trophies = TrophyService.ReadTrophies(File.ReadAllBytes(packagePath), includeIcons);
            TrophyService.ApplyUnlocks(trophies, TrophyService.UnlockFilePath(titleId.ToUpperInvariant()));
            return trophies
                .Select(t => new TrophyEntry(
                    t.Id,
                    t.Grade,
                    t.Name,
                    t.Description,
                    t.Hidden,
                    t.Unlocked,
                    t.Unlocked ? new DateTime(t.UnlockedAt * 10, DateTimeKind.Utc) : null,
                    t.IconPng))
                .ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException
                                       or ArgumentException or InvalidOperationException)
        {
            return [];
        }
    }

    public static bool TryParseUnlockLine(string line, out TrophyUnlockNotice notice)
    {
        notice = null!;
        var start = line.IndexOf(TrophyService.UnlockMarker, StringComparison.Ordinal);
        if (start < 0)
        {
            return false;
        }

        var parts = line[(start + TrophyService.UnlockMarker.Length)..].Split('|', 4);
        if (parts.Length != 4 || !int.TryParse(parts[1], out var id) || !int.TryParse(parts[2], out var grade))
        {
            return false;
        }

        notice = new TrophyUnlockNotice(parts[0], id, grade, parts[3].TrimEnd());
        return true;
    }
}
