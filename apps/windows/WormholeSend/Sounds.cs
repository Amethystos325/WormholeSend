using System.IO;
using System.Windows.Media;
using Microsoft.Win32;

namespace WormholeSend;

/// The moments that sound, as in the macOS app (Sound.swift).
enum Sound
{
    /// The files were delivered.
    Sent,
    /// Files were received.
    Received,
    /// A send failed, or a receive broke off (not when the sender cancelled).
    Failed,
    /// Another device asks to pair.
    PairRequest,
}

/// Plays the app's own sounds (assets/sounds, made by make-sounds.py; the
/// macOS app plays the same files) at 60 % volume. Turned off from the tray
/// menu.
static class Sounds
{
    const string Key = @"Software\WormholeSend";
    const string Value = "Sounds";
    /// Kept until they have played.
    static readonly List<MediaPlayer> playing = [];

    public static bool Enabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(Key);
            return key?.GetValue(Value) is not int on || on != 0;
        }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(Key);
            key.SetValue(Value, value ? 1 : 0, RegistryValueKind.DWord);
        }
    }

    static string FileName(Sound sound) => sound switch
    {
        Sound.Sent => "sent.wav",
        Sound.Received => "received.wav",
        Sound.Failed => "failed.wav",
        _ => "pair-request.wav",
    };

    public static void Play(Sound sound)
    {
        if (!Enabled) return;
        var path = Path.Combine(AppContext.BaseDirectory, "Sounds", FileName(sound));
        if (!File.Exists(path)) return;
        var player = new MediaPlayer();
        playing.Add(player);
        void Done()
        {
            player.Close();
            playing.Remove(player);
        }
        player.MediaEnded += (_, _) => Done();
        player.MediaFailed += (_, _) => Done();
        player.Open(new Uri(path));
        player.Volume = 0.6;
        player.Play();
    }
}
