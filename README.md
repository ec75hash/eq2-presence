# EQ2Presence (smoke test)

Shows the zone your EverQuest II character is in as Discord Rich Presence.
One file of logic (`Program.cs`), one NuGet package (DiscordRichPresence).

## One-time setup

1. Install the .NET SDK (8 or newer): https://dotnet.microsoft.com/download
2. Discord application:
   - https://discord.com/developers/applications -> **New Application**, name it `EverQuest II`
     (that name is the "Playing ..." header people see).
   - General Information -> copy the **Application ID**.
   - Paste it into `DiscordClientId` at the top of `Program.cs`.
   - Optional image: Rich Presence -> Art Assets -> upload, then set `LargeImageKey` to its key.
3. In Discord: Settings -> Activity Privacy -> activity status sharing must be on, or nothing shows.
4. In game: `/log` must be on for the character (it is a per-character toggle).

## Run

```
cd eq2-presence
dotnet run
```

If EQ2 is not in a standard place, pass the logs folder:

```
dotnet run -- "D:\Games\EverQuest II\logs"
```

Zone somewhere. The console prints `zone: <name>` and Discord shows `In <name>` / `<Character> on <Server>`
with an elapsed timer that restarts on each zone change. Ctrl+C clears the presence and exits.

## Testing without Discord or without the game

```
dotnet run -- --no-discord                      # prints the presence instead of sending it
dotnet run -- --replay "C:\...\eq2log_Bob.txt"  # lists every zone change in an old log
```

Run the automated log-reader regression checks (no game or Discord required):

```
dotnet run --project tests/EQ2Presence.RegressionTests.csproj -c Release
```

These checks launch the app against temporary logs and verify live zone updates,
unfinished lines at startup (including split UTF-8 characters), and recovery after
a log is truncated or replaced with shorter content. Only complete, newline-terminated zone messages
are published; an unfinished line is retained until the game finishes writing it.

## Build a single exe

```
dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
```

Output: `bin\Release\net8.0\win-x64\publish\EQ2Presence.exe`. Point a Task Scheduler "at logon" task at it,
with a one-minute delay so Discord is up first.

## How the pieces fit

- `LinePrefix` strips the `(epoch)[date]` prefix every EQ2 log line has.
- `ZoneEntered` matches `You have entered <Zone>.`
- `Watch` polls once a second, follows the log that grew most recently, reads only the new bytes,
  and calls `Push` when the zone changes. Add more patterns next to `ZoneEntered` and more fields to
  `GameState` to grow it.

## Not in the smoke test yet

- Clearing the presence when the game exits (poll for the `EverQuest2` process).
- Combat, target, crafting, level-up lines.
- Multiboxing (foreground window decides which log to follow).
- Tray icon and a pinned-character override.
