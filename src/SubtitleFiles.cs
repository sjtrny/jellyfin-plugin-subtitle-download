using System.Text;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.SubtitleDownload;

public sealed record ExportFormat(string Extension, string Muxer, string Codec);

public static class SubtitleFiles
{
    public static ExportFormat Format(string? codec) => codec?.ToLowerInvariant() switch
    {
        "subrip" or "srt" => new("srt", "srt", "copy"),
        "ass" or "ssa" => new("ass", "ass", "copy"),
        "webvtt" or "vtt" => new("vtt", "webvtt", "copy"),
        "hdmv_pgs_subtitle" or "pgssub" or "pgs" => new("sup", "sup", "copy"),
        "mov_text" or "text" or "subviewer" or "subviewer1" or "microdvd" or "sami" or "smi" or "ttml"
            => new("srt", "srt", "srt"),
        // Matroska preserves bitmap subtitles (including DVD palettes) without OCR.
        _ => new("mks", "matroska", "copy")
    };

    public static string SafeName(string? name)
    {
        var result = new StringBuilder();
        var byteCount = 0;
        foreach (var rune in (name ?? "subtitles").Normalize().EnumerateRunes())
        {
            var text = Rune.IsControl(rune) || (rune.IsAscii && "<>:\"/\\|?*".Contains((char)rune.Value)) ? "_" : rune.ToString();
            var bytes = Encoding.UTF8.GetByteCount(text);
            if (byteCount + bytes > 120) break;
            result.Append(text);
            byteCount += bytes;
        }

        var safe = result.ToString().Trim(' ', '.');
        if (safe.Length == 0) safe = "subtitles";
        string[] reserved = ["CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"];
        if (reserved.Contains(safe.Split('.')[0], StringComparer.OrdinalIgnoreCase)) safe = "_" + safe;
        return safe;
    }

    public static string SafeFileName(string path)
    {
        var extension = Path.GetExtension(path);
        return SafeName(Path.GetFileNameWithoutExtension(path)) + extension;
    }

    public static string TrackName(string stem, MediaStream stream, string extension)
    {
        var language = SafeName(string.IsNullOrWhiteSpace(stream.Language) ? "und" : stream.Language);
        var flags = (stream.IsForced ? ".forced" : "") + (stream.IsHearingImpaired ? ".sdh" : "");
        return $"{SafeName(stem)}.{language}{flags}.track-{stream.Index}.{extension}";
    }

    public static IReadOnlyList<string> ExternalPaths(MediaStream stream)
    {
        if (string.IsNullOrWhiteSpace(stream.Path) || !Path.IsPathRooted(stream.Path) || !File.Exists(stream.Path))
            throw new IOException("The subtitle file is missing or is not stored on this server.");

        var paths = new List<string> { stream.Path };
        var extension = Path.GetExtension(stream.Path);
        if (extension.Equals(".idx", StringComparison.OrdinalIgnoreCase)
            || (extension.Equals(".sub", StringComparison.OrdinalIgnoreCase)
                && (stream.Codec?.Contains("dvd", StringComparison.OrdinalIgnoreCase) == true
                    || stream.Codec?.Contains("vob", StringComparison.OrdinalIgnoreCase) == true)))
        {
            var other = extension.Equals(".idx", StringComparison.OrdinalIgnoreCase) ? ".sub" : ".idx";
            var expected = Path.GetFileNameWithoutExtension(stream.Path) + other;
            var companion = Directory.EnumerateFiles(Path.GetDirectoryName(stream.Path)!)
                .FirstOrDefault(p => Path.GetFileName(p).Equals(expected, StringComparison.OrdinalIgnoreCase));
            if (companion is null) throw new IOException($"The VobSub {other} companion file is missing.");
            paths.Add(companion);
        }

        return paths;
    }
}
