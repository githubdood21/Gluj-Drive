using System.ComponentModel;
using System.Diagnostics;

namespace GlujDrive.Infrastructure.Storage;

internal sealed class FfmpegVideoFrameExtractor(string executablePath)
{
    public async Task<Stream?> ExtractFirstFrameAsync(
        Stream source,
        int maximumDimension,
        CancellationToken cancellationToken)
    {
        if (source is not FileStream fileStream)
        {
            return null;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("-hide_banner");
        startInfo.ArgumentList.Add("-loglevel");
        startInfo.ArgumentList.Add("error");
        startInfo.ArgumentList.Add("-ss");
        startInfo.ArgumentList.Add("0");
        startInfo.ArgumentList.Add("-i");
        startInfo.ArgumentList.Add(fileStream.Name);
        startInfo.ArgumentList.Add("-map");
        startInfo.ArgumentList.Add("0:v:0");
        startInfo.ArgumentList.Add("-frames:v");
        startInfo.ArgumentList.Add("1");
        startInfo.ArgumentList.Add("-vf");
        startInfo.ArgumentList.Add(
            $"scale={maximumDimension}:{maximumDimension}:force_original_aspect_ratio=decrease");
        startInfo.ArgumentList.Add("-f");
        startInfo.ArgumentList.Add("image2pipe");
        startInfo.ArgumentList.Add("-vcodec");
        startInfo.ArgumentList.Add("png");
        startInfo.ArgumentList.Add("pipe:1");

        using var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();
        }
        catch (Win32Exception)
        {
            return null;
        }

        var output = new MemoryStream();
        var copyTask = process.StandardOutput.BaseStream.CopyToAsync(output, cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);

        try
        {
            await Task.WhenAll(copyTask, errorTask, process.WaitForExitAsync(cancellationToken));
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
            output.Dispose();
            throw;
        }

        if (process.ExitCode != 0 || output.Length == 0)
        {
            output.Dispose();
            return null;
        }

        output.Position = 0;
        return output;
    }
}
