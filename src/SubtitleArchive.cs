using System.Diagnostics;
using System.IO.Compression;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SubtitleDownload;

public sealed class ArchiveLease(string directory, int count, int failed) : IDisposable
{
    public FileStream Stream { get; } = new(Path.Combine(directory, "subtitles.zip"), FileMode.Open, FileAccess.Read,
        FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.DeleteOnClose);
    public int Count { get; } = count;
    public int Failed { get; } = failed;
    public void Dispose()
    {
        Stream.Dispose();
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }
}

public sealed class SubtitleArchive(IApplicationPaths paths, IMediaEncoder encoder, ILogger<SubtitleArchive> logger)
{
    private readonly SemaphoreSlim _jobs = new(2);

    public async Task<ArchiveLease> CreateAsync(IReadOnlyList<MediaSourceInfo> sources, CancellationToken cancellationToken)
    {
        await _jobs.WaitAsync(cancellationToken);
        var directory = Path.Combine(paths.CachePath, "subtitle-download", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(directory);
            var included = new List<(string Path, string Name)>();
            var errors = new List<string>();
            var count = 0;
            var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var externalPaths = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            var numberedSources = sources.Select((source, index) => (source, index));
            foreach (var (source, sourceIndex) in numberedSources)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var stem = Path.GetFileNameWithoutExtension(source.Path);
                if (string.IsNullOrEmpty(stem)) stem = source.Name ?? "video";
                var folder = sources.Count > 1 ? $"{sourceIndex + 1:D2}-{SubtitleFiles.SafeName(source.Name ?? stem)}/" : "";
                var embedded = new List<(MediaStream Stream, int InputIndex, string Input, string Path, string Name, ExportFormat Format)>();
                foreach (var stream in source.MediaStreams.Where(s => s.Type == MediaStreamType.Subtitle))
                {
                    try
                    {
                        if (stream.IsExternal)
                        {
                            var files = SubtitleFiles.ExternalPaths(stream);
                            if (files.All(externalPaths.Contains)) continue;
                            foreach (var file in files)
                            {
                                if (!externalPaths.Add(file)) continue;
                                var name = folder + SubtitleFiles.SafeFileName(file);
                                if (!usedNames.Add(name))
                                {
                                    name = folder + SubtitleFiles.TrackName(stem, stream, Path.GetExtension(file).TrimStart('.'));
                                    if (!usedNames.Add(name)) throw new IOException("Two subtitle files have the same export name.");
                                }
                                // Stage a snapshot before sending any part of the ZIP to the browser.
                                var staged = Path.Combine(directory, Guid.NewGuid().ToString("N"));
                                await using var input = File.OpenRead(file);
                                await using var output = File.Create(staged);
                                await input.CopyToAsync(output, cancellationToken);
                                included.Add((staged, name));
                            }
                            count++;
                        }
                        else
                        {
                            var format = SubtitleFiles.Format(stream.Codec);
                            var name = folder + SubtitleFiles.TrackName(stem, stream, format.Extension);
                            if (!usedNames.Add(name)) throw new IOException("Two subtitle tracks have the same export name.");
                            var output = Path.Combine(directory, $"{sourceIndex}-{stream.Index}.{format.Extension}");
                            // Jellyfin's visible indices include external tracks. Use its
                            // own mapping to the stream's actual index in the input file.
                            var inputIndex = EncodingHelper.FindIndex(source.MediaStreams, stream);
                            if (inputIndex < 0) throw new IOException("The subtitle's input stream could not be identified.");
                            embedded.Add((stream, inputIndex, stream.Path ?? source.Path, output, name, format));
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        errors.Add($"{SubtitleFiles.SafeName(stem)} / track {stream.Index} ({stream.Language ?? "und"}): {ex.Message}");
                    }
                }

                foreach (var group in embedded.GroupBy(t => t.Input))
                {
                    try
                    {
                        // One read of each input extracts all its embedded tracks.
                        await ExtractAsync(group.Key, group.Select(t => (t.InputIndex, t.Path, t.Format)).ToArray(), cancellationToken);
                        foreach (var track in group)
                        {
                            if (!File.Exists(track.Path)) throw new IOException("FFmpeg did not produce every subtitle track.");
                        }
                        included.AddRange(group.Select(t => (t.Path, t.Name)));
                        count += group.Count();
                    }
                    catch (Exception ex) when (ex is IOException or TimeoutException)
                    {
                        logger.LogWarning("A subtitle batch failed; retrying each track separately: {Message}", ex.Message);
                        // A broken track must not prevent the remaining tracks from being saved.
                        foreach (var track in group)
                        {
                            try
                            {
                                await ExtractAsync(group.Key, [(track.InputIndex, track.Path, track.Format)], cancellationToken);
                                if (!File.Exists(track.Path)) throw new IOException("FFmpeg did not produce this subtitle track.");
                                included.Add((track.Path, track.Name));
                                count++;
                            }
                            catch (Exception retry) when (retry is IOException or TimeoutException)
                            {
                                errors.Add($"{track.Name}: Could not extract this track ({track.Stream.Codec ?? "unknown codec"}).");
                                logger.LogWarning("Subtitle track {Index} failed: {Message}", track.Stream.Index, retry.Message);
                            }
                        }
                    }
                }
            }

            if (included.Count == 0)
                throw new IOException(errors.Count == 0 ? "No subtitles were detected." : "None of the detected subtitles could be exported. Check that the files are readable and FFmpeg supports their formats.");

            await using (var file = File.Create(Path.Combine(directory, "subtitles.zip")))
            {
                using var zip = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: true);
                foreach (var (path, name) in included)
                {
                    var entry = zip.CreateEntry(name, CompressionLevel.Fastest);
                    await using var source = File.OpenRead(path);
                    await using var target = entry.Open();
                    await source.CopyToAsync(target, cancellationToken);
                }
                if (errors.Count > 0)
                {
                    var entry = zip.CreateEntry("SUBTITLE-EXPORT-ERRORS.txt");
                    await using var text = new StreamWriter(entry.Open());
                    await text.WriteAsync(("Some subtitles could not be exported:\n\n" + string.Join("\n", errors)).AsMemory(), cancellationToken);
                }
            }

            foreach (var (path, _) in included) File.Delete(path);
            return new ArchiveLease(directory, count, errors.Count);
        }
        catch
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
            throw;
        }
        finally
        {
            _jobs.Release();
        }
    }

    private async Task ExtractAsync(string path, (int Index, string Output, ExportFormat Format)[] tracks, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path) || !File.Exists(path))
            throw new IOException("The video is not a readable local file.");

        var info = new ProcessStartInfo(encoder.EncoderPath)
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };
        string[] common = ["-nostdin", "-hide_banner", "-loglevel", "error", "-y", "-threads", "1", "-i", path];
        foreach (var argument in common) info.ArgumentList.Add(argument);
        foreach (var track in tracks)
        {
            string[] args = ["-map", $"0:{track.Index}", "-c:s", track.Format.Codec, "-f", track.Format.Muxer, track.Output];
            foreach (var argument in args) info.ArgumentList.Add(argument);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(10));
        using var process = new Process { StartInfo = info };
        try
        {
            process.Start();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new IOException("Could not start Jellyfin's FFmpeg.", ex);
        }
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            await output;
            var detail = await error;
            if (process.ExitCode != 0)
                throw new IOException($"FFmpeg exited with code {process.ExitCode}: {detail[..Math.Min(1500, detail.Length)]}");
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            try { await Task.WhenAll(error, output); } catch (OperationCanceledException) { }
            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException("Subtitle extraction exceeded ten minutes.");
        }
    }
}
