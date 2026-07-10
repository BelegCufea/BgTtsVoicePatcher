using System.Diagnostics;
using System.IO;

namespace BgTtsVoicePatcher.Audio;

/// <summary>
/// Transcodes a synthesized PCM WAV in place into Ogg Vorbis, written under the same
/// filename. This matches Voices Voices Extravaganza's approach - its ".wav" files
/// are actually OggS streams internally (confirmed via MediaInfo: Format=Ogg, encoded
/// by ffmpeg's libvorbis), which the EE engine accepts since it sniffs the actual
/// audio format rather than trusting the file extension. The encoder itself is also
/// ffmpeg, since System.Speech/SAPI can only emit PCM - there's no way to get Vorbis
/// directly out of speech synthesis.
///
/// Requires ffmpeg (with libvorbis support, which is standard in any normal ffmpeg
/// build) to be installed and reachable - either on PATH or via an explicit path.
/// </summary>
public sealed class OggEncoder
{
    private readonly string _ffmpegPath;
    private readonly int _quality;

    public OggEncoder(string ffmpegPath, int quality)
    {
        _ffmpegPath = ffmpegPath;
        _quality = Math.Clamp(quality, 0, 10);
        Validate();
    }

    private void Validate()
    {
        try
        {
            var (exitCode, _, _) = Run("-version", timeoutMs: 5000);
            if (exitCode != 0)
                throw new InvalidOperationException("ffmpeg did not run successfully.");
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            throw new InvalidOperationException(
                $"Couldn't run ffmpeg at '{_ffmpegPath}'. Install ffmpeg and ensure it's on PATH, " +
                $"or pass --ffmpeg <path-to-ffmpeg.exe>. ({ex.Message})", ex);
        }
    }

    /// <summary>Replaces the PCM WAV at <paramref name="wavPath"/> with an Ogg Vorbis
    /// stream containing the same audio, keeping the same filename/extension.</summary>
    public void EncodeInPlace(string wavPath)
    {
        var tempOggPath = wavPath + ".oggtmp";

        try
        {
            // -f ogg forces the Ogg container muxer regardless of the .wav extension
            // on the output path - ffmpeg would otherwise guess WAV from the name.
            var args = $"-y -i \"{wavPath}\" -c:a libvorbis -qscale:a {_quality} -f ogg \"{tempOggPath}\"";
            var (exitCode, _, stderr) = Run(args, timeoutMs: 30000);

            if (exitCode != 0 || !File.Exists(tempOggPath))
                throw new InvalidOperationException($"ffmpeg failed (exit {exitCode}): {Truncate(stderr, 300)}");

            File.Move(tempOggPath, wavPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempOggPath))
                File.Delete(tempOggPath);
        }
    }

    private (int ExitCode, string StdOut, string StdErr) Run(string arguments, int timeoutMs)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(_ffmpegPath, arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            }
        };

        process.Start();

        // Drain both streams concurrently while waiting, so ffmpeg can't deadlock on a
        // full pipe buffer if it writes more to stdout/stderr than we'd otherwise read.
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        if (!process.WaitForExit(timeoutMs))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"ffmpeg did not exit within {timeoutMs}ms.");
        }

        return (process.ExitCode, stdoutTask.GetAwaiter().GetResult(), stderrTask.GetAwaiter().GetResult());
    }

    private static string Truncate(string text, int max) =>
        text.Length > max ? text[..max] + "..." : text;
}
