using System;
using System.IO;

namespace TourXbox.Core;

/// <summary>
/// 极简日志：只记关键路径和错误，写到 %APPDATA%\TourXbox\app.log。
/// 这个程序以托盘常驻方式运行，出问题时没有控制台可看，留个文件最省事。
/// </summary>
public static class Log
{
    private static readonly object Gate = new();
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "TourXbox",
        "app.log");

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                string? directory = Path.GetDirectoryName(FilePath);
                if (directory != null)
                {
                    Directory.CreateDirectory(directory);
                }

                // 超过 512KB 就截断，避免无限增长
                var info = new FileInfo(FilePath);
                if (info.Exists && info.Length > 512 * 1024)
                {
                    File.WriteAllText(FilePath, string.Empty);
                }

                File.AppendAllText(
                    FilePath,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // 日志失败不能影响主流程
        }
    }
}
