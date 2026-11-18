using Clauderoo.Core;

// Usage: Clauderoo.ConfigWriter <user profile dir> <writer id> <count>
// Adds <count> distinct folder mappings to <dir>\.clauderoo\config.json, each in its own ConfigStore.Update.
if (args.Length != 3 || !int.TryParse(args[2], out var count))
{
    Console.Error.WriteLine("Usage: Clauderoo.ConfigWriter <user profile dir> <writer id> <count>");
    return 2;
}
var paths = new ClauderooPaths(args[0]);
for (var i = 0; i < count; i++)
    ConfigStore.Update(paths, c => { c.Folders[$@"C:\proc-{args[1]}\{i}"] = "personal"; });
return 0;
