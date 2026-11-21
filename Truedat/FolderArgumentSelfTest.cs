using System;
using System.IO;

namespace Truedat
{
    /// <summary>
    /// <c>--folder music</c> means the user's Music folder. A launcher that can only pass fixed
    /// arguments (no shell, so no <c>%USERPROFILE%</c>) can then name the folder without knowing
    /// the user's profile path.
    ///
    /// A real folder of that name always wins: the word is only read as the Music folder when it
    /// is not a folder that exists, which is a case that was an error before.
    /// </summary>
    internal static class FolderArgumentSelfTest
    {
        internal static void Run(Action<bool, string> assert)
        {
            Console.WriteLine("--folder: the word 'music' is the user's Music folder");
            string music = Path.Combine("C:", "Users", "someone", "Music");
            string Special(Environment.SpecialFolder f) => f == Environment.SpecialFolder.MyMusic ? music : "";
            string Resolve(string value, Func<string, bool> exists, out string? note)
                => Program.ResolveFolderArgument(value, exists, Special, out note);

            var got = Resolve("music", _ => false, out var said);
            assert(got == music, $"folder-arg: 'music' is the Music folder ({got})");
            assert(said != null && said.Contains(music) && said.Contains("music"), $"folder-arg: and the run says which folder that turned out to be ({said})");
            assert(Resolve("Music", _ => false, out _) == music && Resolve("MUSIC", _ => false, out _) == music, "folder-arg: whatever the case");
            assert(Resolve("mymusic", _ => false, out _) == music && Resolve("MyMusic", _ => false, out _) == music, "folder-arg: 'MyMusic', the name Windows gives it, is read the same way");

            got = Resolve("music", p => p == "music", out said);
            assert(got == "music" && said == null, $"folder-arg: a folder that really is called music wins, and nothing is said ({got})");

            string real = Path.Combine("D:", "Music");
            got = Resolve(real, _ => true, out said);
            assert(got == real && said == null, "folder-arg: a path is left exactly as typed");
            got = Resolve(real, _ => false, out said);
            assert(got == real && said == null, "folder-arg: a path that does not exist is left for the not-found message, never guessed at");

            got = Program.ResolveFolderArgument("music", _ => false, _ => "", out said);
            assert(got == "music" && said == null, "folder-arg: where Windows reports no Music folder, the word is left as typed");
            got = Resolve("videos", _ => false, out said);
            assert(got == "videos" && said == null, "folder-arg: no other word is given a meaning");

            // ---- --moods library: the catalog beside the library, so the command needs no path of its own
            string drive = "D:" + Path.DirectorySeparatorChar;   // rooted: "D:" alone would be relative to that drive's current folder
            string xml = Path.Combine(drive, "MusicBee", "Library", "iTunes Music Library.xml");
            string beside = Path.Combine(drive, "MusicBee", "Library", Program.MoodsFileName);
            string? M(string? value, Func<string, bool> exists, string? library, out string? note, out string? error)
                => Program.ResolveMoodsArgument(value, exists, library, out note, out error);

            var cat = M("library", _ => false, xml, out var mNote, out var mErr);
            assert(cat == beside && mErr == null, $"moods-arg: 'library' is the catalog beside the library ({cat}; {mErr})");
            assert(mNote != null && mNote.Contains(beside), $"moods-arg: and the run says which file that is ({mNote})");
            assert(M("LIBRARY", _ => false, xml, out _, out _) == beside, "moods-arg: whatever the case");

            cat = M("library", p => p == "library", xml, out mNote, out mErr);
            assert(cat == "library" && mNote == null && mErr == null, "moods-arg: a file that really is called library wins");

            string typed = Path.Combine("D:", "x", "mbxmoods.json");
            cat = M(typed, _ => false, xml, out mNote, out mErr);
            assert(cat == typed && mNote == null && mErr == null, "moods-arg: a path is left exactly as typed, existing or not");
            cat = M(null, _ => false, xml, out mNote, out mErr);
            assert(cat == null && mNote == null && mErr == null, "moods-arg: no --moods stays no --moods; nothing is assumed");

            cat = M("library", _ => false, null, out mNote, out mErr);
            assert(mErr != null && mErr.Contains("library"), $"moods-arg: with no library found the word is an error, never a new file called library ({cat}; {mErr})");

            // The real thing, on whatever machine runs this: Windows either has a Music folder or reports none.
            string here = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
            got = Program.ResolveFolderArgument("music", _ => false, Environment.GetFolderPath, out _);
            assert(got == (string.IsNullOrEmpty(here) ? "music" : here), $"folder-arg: on this machine it reads what Windows reports ({got})");
        }
    }
}
