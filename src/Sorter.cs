using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace Tidybird
{
    public class Move
    {
        public string Source;
        public string DestDir;
        public string Category;
        public DateTime Modified;
    }

    public class SortResult
    {
        public int Moved;
        public int Failed;
        public List<string> Categories = new List<string>();
    }

    // Moves top-level files into <Category>\<date>\ subfolders and keeps a CSV log of
    // every run (same format as Sort-Downloads.ps1) so each run can be undone.
    public static class Sorter
    {
        public static readonly KeyValuePair<string, string[]>[] Categories =
        {
            Cat("Image", "jpg jpeg png gif bmp webp svg heic tif tiff ico raw"),
            Cat("Video", "mp4 mkv mov avi wmv webm flv m4v"),
            Cat("Audio", "mp3 wav flac aac ogg m4a wma opus"),
            Cat("Document", "pdf doc docx odt rtf txt md epub"),
            Cat("Spreadsheet", "xls xlsx ods csv tsv"),
            Cat("Slide", "ppt pptx odp key"),
            Cat("Zip", "zip rar 7z tar gz bz2 xz tgz"),
            Cat("Installer", "exe msi msix appx dmg pkg deb rpm apk iso"),
            Cat("Code", "py js ts json html css java c cpp cs go rs sh ps1 ipynb xml yml yaml sql"),
            Cat("Font", "ttf otf woff woff2"),
            Cat("Torrent", "torrent"),
        };
        public const string OtherCategory = "Other";

        // Folder names used by earlier versions; undo still tidies these up when they empty out
        static readonly string[] LegacyCategories =
        {
            "Images", "Videos", "Audio", "Documents", "Spreadsheets", "Slides", "Archives",
            "Installers", "Code", "Fonts", "Torrents", "Other",
            "Image Downloads", "Video Downloads", "Audio Downloads", "Document Downloads",
            "Spreadsheet Downloads", "Slide Downloads", "Zip Downloads", "Installer Downloads",
            "Code Downloads", "Font Downloads", "Torrent Downloads", "Other Downloads"
        };
        public const string LogDirName = ".sort-logs";

        // The running Tidybird.exe; never moved, even when it sits in the sorted folder
        public static string SelfPath;

        // Partial downloads and temp files: never touch these
        static readonly HashSet<string> SkipExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "crdownload", "part", "partial", "tmp", "download", "opdownload", "!ut"
        };

        static KeyValuePair<string, string[]> Cat(string name, string extensions)
        {
            return new KeyValuePair<string, string[]>(name, extensions.Split(' '));
        }

        public static string GetCategory(string extension)
        {
            string ext = extension.TrimStart('.').ToLowerInvariant();
            foreach (var c in Categories)
                if (c.Value.Contains(ext)) return c.Key;
            return OtherCategory;
        }

        // dateFormat: .NET date format for the date subfolder, or empty for no date folder.
        // Files modified within minAge are left alone so in-progress work is not moved.
        public static List<Move> Plan(string folder, string dateFormat, TimeSpan minAge)
        {
            var cutoff = DateTime.Now - minAge;
            var moves = new List<Move>();
            foreach (string path in Directory.GetFiles(folder))
            {
                if (SelfPath != null && string.Equals(path, SelfPath, StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    var f = new FileInfo(path);
                    if (SkipExtensions.Contains(f.Extension.TrimStart('.'))) continue;
                    if ((f.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
                    if (f.Name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                    DateTime modified = f.LastWriteTime;
                    if (modified > cutoff) continue;

                    string category = GetCategory(f.Extension);
                    string dest = Path.Combine(folder, category);
                    if (!string.IsNullOrEmpty(dateFormat))
                        dest = Path.Combine(dest, modified.ToString(dateFormat, CultureInfo.InvariantCulture));
                    moves.Add(new Move { Source = path, DestDir = dest, Category = category, Modified = modified });
                }
                catch (IOException) { }  // deleted or renamed while scanning
                catch (UnauthorizedAccessException) { }
            }
            return moves.OrderBy(m => m.Category).ThenBy(m => Path.GetFileName(m.Source), StringComparer.OrdinalIgnoreCase).ToList();
        }

        public static SortResult Apply(string folder, List<Move> moves)
        {
            var result = new SortResult();
            if (moves.Count == 0) return result;

            // The log is opened before the first move and written as each file moves,
            // so every move stays undoable even if the run is cut short
            string logPath = NewLogPath(folder);
            using (var log = new StreamWriter(logPath, false, new UTF8Encoding(true)))
            {
                log.WriteLine("\"From\",\"To\"");
                foreach (var m in moves)
                {
                    try
                    {
                        Directory.CreateDirectory(m.DestDir);
                        string dest = GetUniquePath(m.DestDir, Path.GetFileName(m.Source));
                        File.Move(m.Source, dest);
                        log.WriteLine(CsvRow(m.Source, dest));
                        log.Flush();
                        result.Moved++;
                        if (!result.Categories.Contains(m.Category)) result.Categories.Add(m.Category);
                    }
                    catch (Exception)
                    {
                        // Usually a file still open in another program; it is picked up next run
                        result.Failed++;
                    }
                }
            }
            if (result.Moved == 0) File.Delete(logPath);
            return result;
        }

        static string NewLogPath(string folder)
        {
            string logDir = Path.Combine(folder, LogDirName);
            if (!Directory.Exists(logDir))
            {
                var dir = Directory.CreateDirectory(logDir);
                dir.Attributes |= FileAttributes.Hidden;
            }
            // Two runs in the same millisecond must not share a log, so wait for a fresh timestamp
            while (true)
            {
                string path = Path.Combine(logDir,
                    "sort-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture) + ".csv");
                if (!File.Exists(path)) return path;
                System.Threading.Thread.Sleep(2);
            }
        }

        // Windows paths cannot contain quotes, so no escaping is needed
        static string CsvRow(string from, string to)
        {
            return "\"" + from + "\",\"" + to + "\"";
        }

        public static bool CanUndo(string folder)
        {
            return LatestLog(folder) != null;
        }

        public static int Undo(string folder)
        {
            int failed;
            return Undo(folder, out failed);
        }

        // Reverses the most recent run. Returns files restored, or -1 if there is no run to undo.
        // Files that could not be moved back (e.g. open in another program) stay in the log,
        // so the next undo retries them before older runs.
        public static int Undo(string folder, out int failed)
        {
            failed = 0;
            string log = LatestLog(folder);
            if (log == null) return -1;

            // Reverse order so chains of moves unwind correctly
            var rows = File.ReadAllLines(log).Skip(1).Where(l => l.Length > 2).Reverse().ToList();
            var remaining = new List<string>();
            int restored = 0;
            foreach (string line in rows)
            {
                // Windows paths cannot contain quotes, so splitting on "," between quotes is safe
                string[] parts = line.Substring(1, line.Length - 2).Split(new[] { "\",\"" }, StringSplitOptions.None);
                if (parts.Length != 2 || !File.Exists(parts[1])) continue;  // moved or deleted since
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(parts[0]));
                    string back = GetUniquePath(Path.GetDirectoryName(parts[0]), Path.GetFileName(parts[0]));
                    File.Move(parts[1], back);
                    restored++;
                }
                catch (Exception)
                {
                    remaining.Insert(0, line);
                    failed++;
                }
            }

            RemoveEmptyCategoryFolders(folder);
            if (remaining.Count > 0)
                File.WriteAllLines(log, new[] { "\"From\",\"To\"" }.Concat(remaining), new UTF8Encoding(true));
            else
                File.Move(log, Path.Combine(Path.GetDirectoryName(log), Path.GetFileNameWithoutExtension(log) + ".undone.csv"));
            return restored;
        }

        static string LatestLog(string folder)
        {
            string logDir = Path.Combine(folder, LogDirName);
            if (!Directory.Exists(logDir)) return null;
            return Directory.GetFiles(logDir, "sort-*.csv")
                .Where(f => !f.EndsWith(".undone.csv", StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f, StringComparer.Ordinal)
                .LastOrDefault();
        }

        // Only category folders are pruned, never folders the user made
        static void RemoveEmptyCategoryFolders(string folder)
        {
            var roots = Categories.Select(c => c.Key).Concat(new[] { OtherCategory }).Concat(LegacyCategories)
                .Select(c => Path.Combine(folder, c)).Where(Directory.Exists);
            foreach (string root in roots)
            {
                var dirs = Directory.GetDirectories(root, "*", SearchOption.AllDirectories)
                    .Concat(new[] { root }).OrderByDescending(d => d.Length);
                foreach (string d in dirs)
                {
                    try
                    {
                        if (!Directory.EnumerateFileSystemEntries(d).Any()) Directory.Delete(d);
                    }
                    catch (Exception) { }
                }
            }
        }

        // Returns a free path: "name.ext", else "name (1).ext", "name (2).ext", ...
        static string GetUniquePath(string dir, string fileName)
        {
            string target = Path.Combine(dir, fileName);
            string stem = Path.GetFileNameWithoutExtension(fileName);
            string ext = Path.GetExtension(fileName);
            for (int i = 1; File.Exists(target) || Directory.Exists(target); i++)
                target = Path.Combine(dir, stem + " (" + i + ")" + ext);
            return target;
        }
    }
}
