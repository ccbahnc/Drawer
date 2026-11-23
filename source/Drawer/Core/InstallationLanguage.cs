using System;
using System.IO;

namespace Drawer.Core;

// A one-time installation request, not a second live language preference.
// Its revision is committed atomically alongside Global.Language so a later
// in-app change remains effective across restarts, even if the request remains.
internal sealed record InstallationLanguage(DrawerLanguage Language, string Revision)
{
    internal const string FileName = "installation-language.txt";

    internal static InstallationLanguage? Read(MultiDrawerCatalog catalog)
    {
        string path = catalog.Resolve(FileName);
        if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length > 256)
            throw new InvalidDataException("Installation language request is too large.");
        var lines = File.ReadAllLines(path);
        if (lines.Length != 2 || lines[1].Length is < 1 or > 100 ||
            Array.Exists(lines[1].ToCharArray(), ch => !char.IsAsciiLetterOrDigit(ch) && ch != '-' && ch != '.'))
            throw new InvalidDataException("Invalid installation language request.");
        var language = lines[0] switch
        {
            "chinesesimp" => DrawerLanguage.SimplifiedChinese,
            "english" => DrawerLanguage.English,
            _ => throw new InvalidDataException("Unknown installation language.")
        };
        return new(language, lines[1]);
    }
}
