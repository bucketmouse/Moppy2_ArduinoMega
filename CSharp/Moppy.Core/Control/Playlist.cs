using Microsoft.Extensions.Logging;
using Moppy.Core.Midi;
using Moppy.Core.Status;

namespace Moppy.Core.Control;

/// <summary>
/// A list of MIDI files played in order by a <see cref="MoppyMidiSequencer"/>, advancing automatically when each
/// sequence ends (register the playlist with the <see cref="StatusBus"/> for that).
/// </summary>
public sealed class Playlist : IStatusConsumer
{
    private readonly object sync = new();
    private readonly MoppyMidiSequencer sequencer;
    private readonly List<string> files = [];
    private int loadedIndex = -1; // -1 signifies that "nothing" is loaded, and a sequence must be loaded
    private volatile bool repeat;

    public Playlist(MoppyMidiSequencer sequencer)
    {
        this.sequencer = sequencer ?? throw new ArgumentNullException(nameof(sequencer));
    }

    private static ILogger Log => MoppyLogging.CreateLogger<Playlist>();

    /// <summary>Raised when files are added or removed.</summary>
    public event EventHandler? FilesChanged;

    /// <summary>Raised when a different file is loaded (may be raised on the sequencer's thread).</summary>
    public event EventHandler? LoadedIndexChanged;

    public IReadOnlyList<string> Files
    {
        get
        {
            lock (sync)
            {
                return files.ToArray();
            }
        }
    }

    /// <summary>Index of the loaded file, or -1 if the playlist hasn't loaded anything (since its files last changed).</summary>
    public int LoadedIndex
    {
        get
        {
            lock (sync)
            {
                return loadedIndex;
            }
        }
    }

    /// <summary>Repeats the playlist after the last (or only) song has finished.</summary>
    public bool Repeat
    {
        get => repeat;
        set => repeat = value;
    }

    public void Add(string path) => AddRange([path]);

    public void AddRange(IEnumerable<string> paths)
    {
        lock (sync)
        {
            files.AddRange(paths);
        }

        FilesChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Removes the files at the given indices.</summary>
    public void RemoveAt(IEnumerable<int> indices)
    {
        lock (sync)
        {
            foreach (int index in indices.Distinct().OrderDescending())
            {
                if (index >= 0 && index < files.Count)
                {
                    files.RemoveAt(index);
                }
            }

            loadedIndex = -1; // We've removed things, so the index needs to be reset
        }

        FilesChanged?.Invoke(this, EventArgs.Empty);
        LoadedIndexChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Clear() => RemoveAt(Enumerable.Range(0, Files.Count));

    /// <summary>
    /// Loads the file at the given index into the sequencer.  If it can't be loaded, the following files are tried instead.
    /// </summary>
    /// <returns>True if a file was loaded.</returns>
    public bool LoadSequence(int playlistIndex)
    {
        for (int index = playlistIndex; ; index++)
        {
            string file;
            lock (sync)
            {
                if (index < 0 || index >= files.Count)
                {
                    return false;
                }

                file = files[index];
            }

            try
            {
                sequencer.LoadSequence(file);
                lock (sync)
                {
                    loadedIndex = index;
                }

                LoadedIndexChanged?.Invoke(this, EventArgs.Empty);
                return true;
            }
            catch (Exception ex) when (ex is IOException or InvalidMidiDataException or UnauthorizedAccessException)
            {
                Log.LogWarning(ex, "Unable to load {File}", file);
                // Try to load next file instead
            }
        }
    }

    /// <summary>Loads the file at the given index and starts playing it (e.g. when it's double-clicked).</summary>
    public void PlayIndex(int playlistIndex)
    {
        LoadSequence(playlistIndex);
        if (sequencer.IsSequenceLoaded)
        {
            sequencer.Play();
        }
    }

    /// <summary>
    /// Pauses if playing; otherwise starts playing (loading the first file if nothing is loaded yet).
    /// </summary>
    public void TogglePlayPause()
    {
        if (sequencer.IsPlaying)
        {
            sequencer.Pause();
            return;
        }

        if (Files.Count == 0)
        {
            return;
        }

        if (!sequencer.IsSequenceLoaded || LoadedIndex < 0)
        {
            LoadSequence(0); // Load the first song
        }

        if (sequencer.IsSequenceLoaded)
        {
            sequencer.Play();
        }
    }

    /// <summary>
    /// Advances the loaded sequence to the next in the playlist and plays it, or wraps back to the first
    /// (playing it only if <see cref="Repeat"/> is set).
    /// </summary>
    public void Advance()
    {
        int next;
        int count;
        lock (sync)
        {
            next = loadedIndex + 1;
            count = files.Count;
        }

        if (next >= count)
        {
            if (count > 0)
            {
                LoadSequence(0);
            }

            if (Repeat && sequencer.IsSequenceLoaded && count > 0)
            {
                sequencer.Play();
            }
            else
            {
                sequencer.Stop();
            }
        }
        else
        {
            LoadSequence(next);
            sequencer.Play();
        }
    }

    public void ReceiveUpdate(StatusUpdate update)
    {
        if (update.Type == StatusType.SequenceEnd)
        {
            Advance();
        }
    }
}
