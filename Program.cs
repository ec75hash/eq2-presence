// EQ2Presence, smoke test.
// Shows the zone your EverQuest II character is in as Discord Rich Presence.
//
// How it works:
//   1. Finds the EQ2 logs folder:  logs\<Server>\eq2log_<Character>.txt
//   2. Follows whichever log file is currently growing. With one game client that is the character you are playing.
//   3. Watches for "You have entered <Zone>." lines and pushes  In <Zone>  /  <Character> on <Server>  to Discord.
//
// Logging must be ON for the character (type /log in game). The Discord desktop client must be running.
//
// Command line:
//   EQ2Presence [logs folder]            run it (folder optional if EQ2 is installed in a standard place)
//   EQ2Presence --no-discord             same, but print the presence instead of sending it to Discord
//   EQ2Presence --replay <eq2log file>   list every zone change in an existing log, then exit (tests the patterns)

using System.Text;
using System.Text.RegularExpressions;
using DiscordRPC;
using DiscordRPC.Logging;

namespace EQ2Presence;

internal static class Program
{
    // ============ Settings ============

    // 1. Go to https://discord.com/developers/applications and click "New Application". Name it "EverQuest II";
    //    that name becomes the "Playing ..." header in Discord.
    // 2. On General Information, copy the Application ID (a long number) and paste it here.
    private const string DiscordClientId = "PASTE_YOUR_APPLICATION_ID_HERE";

    // Optional. Under Rich Presence -> Art Assets upload an image, then put its key here (for example "eq2").
    // Leave empty for no image.
    private const string LargeImageKey = "";

    // Where EQ2 writes logs. The first folder that exists is used, unless you pass a folder on the command line.
    private static readonly string[] DefaultLogRoots =
    {
        @"C:\Users\Public\Daybreak Game Company\Installed Games\EverQuest II\logs",
        @"C:\Program Files (x86)\Steam\steamapps\common\EverQuest II\logs",
    };

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    // How far back (bytes) to look in the log for the last zone line when the program starts.
    private const int TailBytes = 512 * 1024;

    // ============ Log line patterns ============

    // Every EQ2 log line looks like:   (1140157349)[Fri Feb 17 06:22:29 2006] the message
    private static readonly Regex LinePrefix =
        new(@"^\((?<epoch>\d+)\)\[(?<date>[^\]]*)\]\s?(?<text>.*)$", RegexOptions.Compiled);

    // Zone change message:   You have entered Antonica.
    private static readonly Regex ZoneEntered =
        new(@"^You have entered (?<zone>.+?)\.?\s*$", RegexOptions.Compiled);

    // ============ Program ============

    private static volatile bool _stop;
    private static DateTime _lastDiscordWarning = DateTime.MinValue;

    private static int Main(string[] args)
    {
        bool noDiscord = false;
        string? replayFile = null;
        var positional = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--no-discord": noDiscord = true; break;
                case "--replay": replayFile = i + 1 < args.Length ? args[++i] : null; break;
                default: positional.Add(args[i]); break;
            }
        }

        if (replayFile != null)
            return Replay(replayFile);

        string? logsRoot = positional.FirstOrDefault() ?? DefaultLogRoots.FirstOrDefault(Directory.Exists);
        if (logsRoot == null || !Directory.Exists(logsRoot))
        {
            Console.Error.WriteLine("Could not find the EQ2 logs folder. Pass it on the command line, for example:");
            Console.Error.WriteLine(@"  dotnet run -- ""C:\Users\Public\Daybreak Game Company\Installed Games\EverQuest II\logs""");
            return 1;
        }

        if (!noDiscord && (DiscordClientId.Length < 10 || !DiscordClientId.All(char.IsDigit)))
        {
            Console.Error.WriteLine("Set DiscordClientId at the top of Program.cs to your Discord Application ID,");
            Console.Error.WriteLine("or run with --no-discord to test the log reading on its own.");
            return 1;
        }

        Console.CancelKeyPress += (_, e) => { e.Cancel = true; _stop = true; };

        using DiscordRpcClient? discord = noDiscord ? null : CreateDiscordClient();

        Console.WriteLine($"Logs folder: {logsRoot}");
        Console.WriteLine(noDiscord ? "Discord: off (--no-discord)" : "Discord: connecting (the desktop client must be running)");
        Console.WriteLine("Press Ctrl+C to quit.");

        Watch(logsRoot, discord);

        discord?.ClearPresence();
        Console.WriteLine("Stopped.");
        return 0;
    }

    private static DiscordRpcClient CreateDiscordClient()
    {
        var client = new DiscordRpcClient(DiscordClientId)
        {
            Logger = new ConsoleLogger(LogLevel.Warning, true),
        };
        client.OnReady += (_, e) => Console.WriteLine($"Discord: connected as {e.User.Username}");
        client.OnConnectionFailed += (_, _) =>
        {
            // The library keeps retrying on its own (backoff up to a minute). Only nag once a minute.
            if (DateTime.UtcNow - _lastDiscordWarning > TimeSpan.FromMinutes(1))
            {
                _lastDiscordWarning = DateTime.UtcNow;
                Console.WriteLine("Discord: not reachable yet (is the desktop client running?), retrying");
            }
        };
        client.Initialize();
        return client;
    }

    // ============ The watch loop ============

    private static void Watch(string logsRoot, DiscordRpcClient? discord)
    {
        var lastSize = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var lastGrowth = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? active = null;
        long position = 0;
        var carry = new List<byte>(); // bytes of a line the game has not finished writing yet
        var current = new GameState();

        while (!_stop)
        {
            try
            {
                // 1. Which log is alive? The one whose size changed most recently.
                seen.Clear();
                string? liveliest = null;
                DateTime liveliestAt = DateTime.MinValue;
                foreach (string file in FindLogs(logsRoot))
                {
                    seen.Add(file);
                    long size = FileSize(file);
                    if (lastSize.TryGetValue(file, out long previous) && size != previous)
                        lastGrowth[file] = DateTime.UtcNow;
                    lastSize[file] = size;

                    if (lastGrowth.TryGetValue(file, out DateTime at) && at > liveliestAt)
                    {
                        liveliestAt = at;
                        liveliest = file;
                    }
                }

                // Forget logs that were deleted.
                foreach (string gone in lastSize.Keys.Where(f => !seen.Contains(f)).ToList())
                {
                    lastSize.Remove(gone);
                    lastGrowth.Remove(gone);
                    if (string.Equals(gone, active, StringComparison.OrdinalIgnoreCase))
                        active = null;
                }

                // Until something grows (the first second or two), guess from the most recently written file.
                string? chosen = liveliest ?? lastSize.Keys.OrderByDescending(f => File.GetLastWriteTimeUtc(f)).FirstOrDefault();

                if (chosen != null && !string.Equals(chosen, active, StringComparison.OrdinalIgnoreCase))
                {
                    active = chosen;
                    position = lastSize[active];
                    carry.Clear();
                    (current.Server, current.Character) = ServerAndCharacter(active);
                    current.Zone = LastZoneIn(active);
                    current.ZoneSince = DateTime.UtcNow;
                    string fileName = Path.GetFileName(active);
                    Console.WriteLine($"{Clock()} following {current.Character} on {current.Server} ({fileName})");
                    Push(discord, current);
                }

                // 2. Read whatever the active log gained since last time.
                if (active != null)
                {
                    long size = lastSize[active];
                    if (size < position)
                    {
                        // The file shrank (deleted and recreated, or truncated). Start over from its end.
                        position = size;
                        carry.Clear();
                    }
                    if (size > position)
                    {
                        foreach (string line in ReadNewLines(active, ref position, carry))
                        {
                            if (TryZone(line, out string zone) && zone != current.Zone)
                            {
                                current.Zone = zone;
                                current.ZoneSince = DateTime.UtcNow;
                                Console.WriteLine($"{Clock()} zone: {zone}");
                                Push(discord, current);
                            }
                        }
                    }
                }
            }
            catch (IOException ex)
            {
                Console.WriteLine($"{Clock()} file hiccup, will retry: {ex.Message}");
            }

            Thread.Sleep(PollInterval);
        }
    }

    // ============ Presence ============

    private sealed class GameState
    {
        public string Server { get; set; } = "";
        public string Character { get; set; } = "";
        public string? Zone { get; set; }
        public DateTime ZoneSince { get; set; } = DateTime.UtcNow;
    }

    private static void Push(DiscordRpcClient? discord, GameState s)
    {
        string details = s.Zone == null ? "Zone not known yet" : $"In {s.Zone}";
        string state = $"{s.Character} on {s.Server}";

        if (discord == null)
        {
            Console.WriteLine($"{Clock()} presence -> \"{details}\" / \"{state}\"");
            return;
        }

        var presence = new RichPresence
        {
            Details = Clip(details),
            State = Clip(state),
            Timestamps = new Timestamps(s.ZoneSince), // Discord shows this as elapsed time in the zone
        };
        if (LargeImageKey.Length > 0)
        {
            presence.Assets = new Assets
            {
                LargeImageKey = LargeImageKey,
                LargeImageText = "EverQuest II",
            };
        }
        discord.SetPresence(presence);
    }

    // Discord accepts 2 to 128 characters for details and state.
    private static string Clip(string text) => text.Length > 128 ? text.Substring(0, 128) : text;

    // ============ Log files ============

    private static IEnumerable<string> FindLogs(string logsRoot)
    {
        foreach (string serverDir in Directory.EnumerateDirectories(logsRoot))
        {
            foreach (string file in Directory.EnumerateFiles(serverDir, "eq2log_*.txt"))
                yield return file;
        }
    }

    // logs\Maj'Dul\eq2log_Bob.txt  ->  ("Maj'Dul", "Bob")
    private static (string Server, string Character) ServerAndCharacter(string path)
    {
        string server = Path.GetFileName(Path.GetDirectoryName(path) ?? "");
        string name = Path.GetFileNameWithoutExtension(path);
        string character = name.StartsWith("eq2log_", StringComparison.OrdinalIgnoreCase) ? name.Substring(7) : name;
        return (server, character);
    }

    // The game keeps the log open for writing, so share read, write and delete or the open fails.
    private static FileStream OpenShared(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    // Open a handle instead of trusting FileInfo.Length: Windows can report a stale size for a file
    // another process holds open.
    private static long FileSize(string path)
    {
        using FileStream stream = OpenShared(path);
        return stream.Length;
    }

    // Reads from `position` to the end of the file. Returns finished lines; keeps an unfinished last line in `carry`.
    private static List<string> ReadNewLines(string path, ref long position, List<byte> carry)
    {
        var lines = new List<string>();
        using FileStream stream = OpenShared(path);
        stream.Seek(position, SeekOrigin.Begin);
        var buffer = new byte[64 * 1024];
        int count;
        while ((count = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            position += count;
            for (int i = 0; i < count; i++)
            {
                if (buffer[i] == (byte)'\n')
                {
                    lines.Add(Encoding.UTF8.GetString(carry.ToArray()).TrimEnd('\r'));
                    carry.Clear();
                }
                else
                {
                    carry.Add(buffer[i]);
                }
            }
        }
        return lines;
    }

    // Look back through the end of the log for the most recent zone line, so the presence is right immediately.
    private static string? LastZoneIn(string path)
    {
        using FileStream stream = OpenShared(path);
        long length = stream.Length;
        long start = Math.Max(0, length - TailBytes);
        stream.Seek(start, SeekOrigin.Begin);
        var bytes = new byte[(int)(length - start)];
        int total = 0;
        while (total < bytes.Length)
        {
            int count = stream.Read(bytes, total, bytes.Length - total);
            if (count <= 0) break;
            total += count;
        }

        string[] lines = Encoding.UTF8.GetString(bytes, 0, total).Split('\n');
        int first = start > 0 ? 1 : 0; // when we started mid-file the first piece is half a line; skip it
        for (int i = lines.Length - 1; i >= first; i--)
        {
            if (TryZone(lines[i].TrimEnd('\r'), out string zone))
                return zone;
        }
        return null;
    }

    private static bool TryZone(string line, out string zone)
    {
        zone = "";
        Match prefix = LinePrefix.Match(line);
        if (!prefix.Success) return false;
        Match m = ZoneEntered.Match(prefix.Groups["text"].Value);
        if (!m.Success) return false;
        zone = m.Groups["zone"].Value;
        return true;
    }

    // --replay <logfile>: print every zone change in an existing log, to check the patterns without the game running.
    private static int Replay(string file)
    {
        if (!File.Exists(file))
        {
            Console.Error.WriteLine($"No such file: {file}");
            return 1;
        }
        int found = 0;
        foreach (string line in File.ReadLines(file))
        {
            if (!TryZone(line, out string zone)) continue;
            found++;
            string date = LinePrefix.Match(line).Groups["date"].Value;
            Console.WriteLine($"[{date}] {zone}");
        }
        Console.WriteLine($"{found} zone change(s) in {file}");
        return 0;
    }

    private static string Clock() => DateTime.Now.ToString("HH:mm:ss");
}
