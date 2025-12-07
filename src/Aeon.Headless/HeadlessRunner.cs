namespace Aeon.Headless;

using Aeon.DiskImages;
using Aeon.Emulator;
using Aeon.Emulator.Dos.VirtualFileSystem;
using Aeon.Emulator.Launcher.Configuration;
using Aeon.Emulator.Sound;

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using ErrorEventArgs = Aeon.Emulator.ErrorEventArgs;

#pragma warning disable CA1416 // Validate platform compatibility
#pragma warning disable IL2070, IL2075 // Reflection access for DOS internals

/// <summary>
/// Minimal host for running Aeon headless with an <see cref="AeonConfiguration"/> or the
/// simpler <see cref="HeadlessAeonConfig"/>.
/// Drop this file into a project that already references the compiled Aeon assemblies.
/// </summary>
public sealed class HeadlessRunner : IAsyncDisposable, IDisposable
{
    private readonly EmulatorHost _host;
    private readonly TaskCompletionSource<EmulatorState> _exit_tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<IDisposable> _disposables = [];
    private bool _disposed;

    public HeadlessRunner(HeadlessAeonConfig config)
        : this(config.ToAeonConfiguration())
    {
    }

    public HeadlessRunner(AeonConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);

        Configuration = config;
        _host = new EmulatorHost(config.PhysicalMemorySize ?? 16);
        ApplyConfiguration();
    }

    public AeonConfiguration Configuration { get; }
    public VirtualMachine VirtualMachine => _host.VirtualMachine;
    public EmulatorState State => _host.State;
    public event Action<string>? LineReceived;

    public static Task<EmulatorState> RunConfigAsync(string configPath, CancellationToken cancellationToken = default) =>
        RunAsync(AeonConfiguration.Load(configPath), cancellationToken);

    public static Task<EmulatorState> RunQuickAsync(string hostPath, string? launch, CancellationToken cancellationToken = default) =>
        RunAsync(AeonConfiguration.GetQuickLaunchConfiguration(hostPath, launch ?? string.Empty), cancellationToken);

    public static async Task<EmulatorState> RunAsync(AeonConfiguration configuration, CancellationToken cancellationToken = default)
    {
        await using var runner = new HeadlessRunner(configuration);
        return await runner.RunAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<EmulatorState> RunAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        using var _ = cancellationToken.Register(() =>
        {
            try { _host.Halt(); } catch { /* ignore */ }
            _exit_tcs.TrySetCanceled(cancellationToken);
        });

        _host.StateChanged += OnStateChanged;
        _host.Error += OnError;

        try
        {
            LoadLaunchTarget();
            _host.EmulationSpeed = Configuration.EmulationSpeed ?? 100_000_000;
            _host.Run();

            return await _exit_tcs.Task.ConfigureAwait(false);
        }
        finally
        {
            _host.StateChanged -= OnStateChanged;
            _host.Error -= OnError;
        }
    }

    /// <summary>
    /// Sends text as if typed at the DOS prompt (very simple key mapping).
    /// </summary>
    public async Task SendTextAsync(string text, TimeSpan? perKeyDelay = null, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (string.IsNullOrEmpty(text))
            return;

        var delay = perKeyDelay ?? TimeSpan.FromMilliseconds(10);

        foreach (var ch in text)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryMapCharToKey(ch, out var key, out bool withShift))
                continue;

            if (withShift)
                _host.PressKey(Keys.LeftShift);

            _host.PressKey(key);
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            _host.ReleaseKey(key);

            if (withShift)
                _host.ReleaseKey(Keys.LeftShift);
        }
    }

    /// <summary>
    /// Sends a command line and appends a newline (Enter).
    /// </summary>
    public Task SendCommandAsync(string text, TimeSpan? perKeyDelay = null, CancellationToken cancellationToken = default) =>
        SendTextAsync((text ?? string.Empty) + "\n", perKeyDelay, cancellationToken);

    /// <summary>
    /// Sends multiple newlines followed by EXIT and a final newline.
    /// </summary>
    public Task SendExitAsync(TimeSpan? perKeyDelay = null, CancellationToken cancellationToken = default) =>
        SendTextAsync("\n\n\nEXIT\n", perKeyDelay, cancellationToken);

    /// <summary>
    /// Tap stdout/stderr so each text line is raised via <see cref="LineReceived"/>.
    /// This uses reflection to swap the DOS stdout handle for a small wrapper stream.
    /// </summary>
    public void AttachConsoleLineTap()
    {
        ThrowIfDisposed();

        var vm = _host.VirtualMachine;
        var dos = typeof(VirtualMachine).GetProperty("Dos", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(vm)
                  ?? throw new InvalidOperationException("Unable to access DOS handler.");

        var fileControlField = dos.GetType().GetField("fileControl", BindingFlags.NonPublic | BindingFlags.Instance)
                                  ?? throw new InvalidOperationException("Unable to access DOS file control.");
        var fileControl = fileControlField.GetValue(dos)
                          ?? throw new InvalidOperationException("DOS file control missing.");

        var fileHandlesField = fileControl.GetType().GetField("fileHandles", BindingFlags.NonPublic | BindingFlags.Instance)
                              ?? throw new InvalidOperationException("Unable to access DOS file handles.");

        var openFileDict = fileHandlesField.GetValue(fileControl)
                          ?? throw new InvalidOperationException("DOS file handles missing.");

        var tryGetValue = openFileDict.GetType().GetMethod("TryGetValue")
                          ?? throw new InvalidOperationException("Cannot locate TryGetValue on file handle dictionary.");

        if (!TryGetDosStream(openFileDict, tryGetValue, 1, out var stdout) ||
            !TryGetDosStream(openFileDict, tryGetValue, 2, out var stderr))
            throw new InvalidOperationException("Unable to get stdout/stderr handles.");

        var stdoutInfo = stdout is null ? null : GetFileInfo(stdout);
        var stderrInfo = stderr is null ? null : GetFileInfo(stderr);
        var stdoutOwner = stdout is null ? -1 : GetOwnerId(stdout);
        var stderrOwner = stderr is null ? -1 : GetOwnerId(stderr);

        var add = FindAddMethod(openFileDict.GetType(), stdout!.GetType())
                  ?? throw new InvalidOperationException("Unable to locate Add on file handle dictionary.");
        var tapStdout = BuildTappedStream(stdout, stdoutOwner, stdoutInfo, line => LineReceived?.Invoke(line));
        var tapStderr = BuildTappedStream(stderr, stderrOwner, stderrInfo, line => LineReceived?.Invoke(line));

        if (tapStdout is null || tapStderr is null)
            throw new InvalidOperationException("Unable to build tapped streams.");

        var addParams = add.GetParameters().Length;
        var stdoutArgs = addParams == 4
            ? new object?[] { (short)1, tapStdout, stdoutInfo, true }
            : new object?[] { (short)1, tapStdout, stdoutInfo };
        var stderrArgs = addParams == 4
            ? new object?[] { (short)2, tapStderr, stderrInfo, true }
            : new object?[] { (short)2, tapStderr, stderrInfo };

        add.Invoke(openFileDict, stdoutArgs);
        add.Invoke(openFileDict, stderrArgs);
    }

    private static MethodInfo? FindAddMethod(Type dictType, Type dosStreamType)
    {
        foreach (var m in dictType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
        {
            if (!string.Equals(m.Name, "Add", StringComparison.Ordinal))
                continue;

            var p = m.GetParameters();
            if (p.Length is < 3 or > 4)
                continue;
            if (p[0].ParameterType != typeof(short))
                continue;
            if (!p[1].ParameterType.IsAssignableFrom(dosStreamType))
                continue;

            return m;
        }

        return null;
    }

    private void ApplyConfiguration()
    {
        var vm = _host.VirtualMachine;
        var global = GlobalConfiguration.Load();

        foreach (var (letter, info) in Configuration.Drives)
        {
            if (string.IsNullOrEmpty(letter) || letter.Length != 1)
                throw new FormatException($"Drive key '{letter}' is invalid.");

            var driveLetter = new DriveLetter(char.ToUpperInvariant(letter[0]));
            var vmDrive = vm.FileSystem.Drives[driveLetter];

            vmDrive.DriveType = info.Type;
            vmDrive.VolumeLabel = info.Label;
            if (info.FreeSpace.HasValue)
                vmDrive.FreeSpace = info.FreeSpace.Value;

            if (!string.IsNullOrWhiteSpace(info.HostPath))
            {
                var hostPath = Path.GetFullPath(info.HostPath);
                vmDrive.Mapping = info.ReadOnly ? new MappedFolder(hostPath) : new WritableMappedFolder(hostPath);
            }
            else if (!string.IsNullOrWhiteSpace(info.ImagePath))
            {
                var imagePath = Path.GetFullPath(info.ImagePath);
                var ext = Path.GetExtension(imagePath);
                if (ext.Equals(".iso", StringComparison.OrdinalIgnoreCase))
                    vmDrive.Mapping = new ISOImage(imagePath);
                else if (ext.Equals(".cue", StringComparison.OrdinalIgnoreCase))
                    vmDrive.Mapping = new CueSheetImage(imagePath);
                else
                    throw new FormatException($"Unsupported image type: {info.ImagePath}");
            }
            else
            {
                throw new FormatException($"Drive {letter} is missing a host-path or image-path.");
            }

            vmDrive.HasCommandInterpreter = vmDrive.DriveType == Aeon.Emulator.DriveType.Fixed;
        }

        if (!string.IsNullOrWhiteSpace(Configuration.StartupPath))
            vm.FileSystem.WorkingDirectory = new VirtualPath(Configuration.StartupPath);

        // Disabled for now - headless use cases may not need sound or joystick support.
        //vm.RegisterVirtualDevice(new Aeon.Emulator.Sound.PCSpeaker.InternalSpeaker());
        //vm.RegisterVirtualDevice(new Aeon.Emulator.Sound.Blaster.SoundBlaster(vm));
        //vm.RegisterVirtualDevice(new Aeon.Emulator.Sound.FM.FmSoundCard());
        //vm.RegisterVirtualDevice(new Aeon.Emulator.Sound.GeneralMidi(
        //	new GeneralMidiOptions(
        //		Configuration.MidiEngine ?? global.MidiEngine ?? MidiEngine.MidiMapper,
        //		global.SoundFontPath,
        //		global.Mt32RomsPath)));
        //vm.RegisterVirtualDevice(new Aeon.Emulator.Input.JoystickDevice());
    }

    private void LoadLaunchTarget()
    {
        var vm = _host.VirtualMachine;
        var launch = Configuration.Launch;

        if (string.IsNullOrWhiteSpace(launch))
        {
            var commandPath = vm.FileSystem.CommandInterpreterPath?.ToString() ?? "COMMAND.COM";
            _host.LoadProgram(commandPath);
            return;
        }

        var launchParts = launch.Split([' ', '\t'], 2, StringSplitOptions.RemoveEmptyEntries);
        if (launchParts.Length == 1)
            _host.LoadProgram(launchParts[0]);
        else
            _host.LoadProgram(launchParts[0], launchParts[1]);
    }

    private static bool TryMapCharToKey(char ch, out Keys key, out bool needsShift)
    {
        needsShift = false;
        switch (ch)
        {
            case >= 'A' and <= 'Z':
                key = Enum.Parse<Keys>(ch.ToString(), ignoreCase: false);
                needsShift = true;
                return true;
            case >= 'a' and <= 'z':
                key = Enum.Parse<Keys>(ch.ToString(), ignoreCase: true);
                return true;
            case >= '0' and <= '9':
                key = ch switch
                {
                    '0' => Keys.Zero,
                    '1' => Keys.One,
                    '2' => Keys.Two,
                    '3' => Keys.Three,
                    '4' => Keys.Four,
                    '5' => Keys.Five,
                    '6' => Keys.Six,
                    '7' => Keys.Seven,
                    '8' => Keys.Eight,
                    '9' => Keys.Nine,
                    _ => Keys.Null
                };
                return key != Keys.Null;
            case '!':
                key = Keys.One;
                needsShift = true;
                return true;
            case '@':
                key = Keys.Two;
                needsShift = true;
                return true;
            case '#':
                key = Keys.Three;
                needsShift = true;
                return true;
            case '$':
                key = Keys.Four;
                needsShift = true;
                return true;
            case '%':
                key = Keys.Five;
                needsShift = true;
                return true;
            case '^':
                key = Keys.Six;
                needsShift = true;
                return true;
            case '&':
                key = Keys.Seven;
                needsShift = true;
                return true;
            case '*':
                key = Keys.Eight;
                needsShift = true;
                return true;
            case '(':
                key = Keys.Nine;
                needsShift = true;
                return true;
            case ')':
                key = Keys.Zero;
                needsShift = true;
                return true;
            case ' ':
                key = Keys.Space;
                return true;
            case '\t':
                key = Keys.Tab;
                return true;
            case '\b':
                key = Keys.Backspace;
                return true;
            case '\r':
            case '\n':
                key = Keys.Enter;
                return true;
            case '-':
                key = Keys.Minus;
                return true;
            case '_':
                key = Keys.Minus;
                needsShift = true;
                return true;
            case '=':
                key = Keys.Equals;
                return true;
            case '+':
                key = Keys.Equals;
                needsShift = true;
                return true;
            case '[':
                key = Keys.OpenBracket;
                return true;
            case '{':
                key = Keys.OpenBracket;
                needsShift = true;
                return true;
            case ']':
                key = Keys.CloseBracket;
                return true;
            case '}':
                key = Keys.CloseBracket;
                needsShift = true;
                return true;
            case '\\':
                key = Keys.Backslash;
                return true;
            case '|':
                key = Keys.Backslash;
                needsShift = true;
                return true;
            case ';':
                key = Keys.Semicolon;
                return true;
            case ':':
                key = Keys.Semicolon;
                needsShift = true;
                return true;
            case '\'':
                key = Keys.Apostrophe;
                return true;
            case '"':
                key = Keys.Apostrophe;
                needsShift = true;
                return true;
            case '`':
                key = Keys.GraveApostrophe;
                return true;
            case '~':
                key = Keys.GraveApostrophe;
                needsShift = true;
                return true;
            case ',':
                key = Keys.Comma;
                return true;
            case '<':
                key = Keys.Comma;
                needsShift = true;
                return true;
            case '.':
                key = Keys.Period;
                return true;
            case '>':
                key = Keys.Period;
                needsShift = true;
                return true;
            case '/':
                key = Keys.Slash;
                return true;
            case '?':
                key = Keys.Slash;
                needsShift = true;
                return true;
            default:
                key = Keys.Null;
                return false;
        }
    }

    private static bool TryGetDosStream(object openFileDict, MethodInfo tryGetValue, short handle, out object? dosStream)
    {
        var args = new object?[] { handle, null };
        var ok = (bool)tryGetValue.Invoke(openFileDict, args)!;
        dosStream = args[1];
        return ok && dosStream != null;
    }

    private static object? BuildTappedStream(object? sourceDosStream, int ownerId, object? fileInfo, Action<string>? onLine)
    {
        if (sourceDosStream is null)
            return null;

        var baseStreamProp = sourceDosStream.GetType().GetProperty("BaseStream")!;
        var baseStream = (Stream)baseStreamProp.GetValue(sourceDosStream)!;

        var tapped = new ConsoleTapStream(baseStream);
        if (onLine != null)
            tapped.LineEmitted += onLine;

        var dosStreamType = sourceDosStream.GetType();
        var ctor = dosStreamType.GetConstructor([typeof(Stream), typeof(int)])
                   ?? throw new InvalidOperationException("Cannot find DOS stream constructor.");
        var newDs = ctor.Invoke([tapped, ownerId]);

        if (fileInfo != null)
        {
            var fileInfoProp = dosStreamType.GetProperty("FileInfo");
            fileInfoProp?.SetValue(newDs, fileInfo);
        }

        return newDs;
    }

    private static object? GetFileInfo(object dosStream)
    {
        return dosStream.GetType().GetProperty("FileInfo")?.GetValue(dosStream);
    }

    private static int GetOwnerId(object dosStream)
    {
        return (int)(dosStream.GetType().GetProperty("OwnerId")?.GetValue(dosStream) ?? 0);
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        var state = _host.State;
        if (state is EmulatorState.ProgramExited or EmulatorState.Halted)
            _exit_tcs.TrySetResult(state);
    }

    private void OnError(object? sender, ErrorEventArgs e) =>
        _exit_tcs.TrySetException(new InvalidOperationException(e.Message));

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _host.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;
        foreach (var d in _disposables)
            d.Dispose();
        await _host.DisposeAsync().ConfigureAwait(false);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, nameof(HeadlessRunner));
    }
}

/// <summary>
/// Simple strongly-typed config you can new-up instead of reading a .AeonConfig file.
/// </summary>
public sealed class HeadlessAeonConfig
{
    public string StartupPath { get; set; } = @"C:\";
    public string? Launch { get; set; }
    public bool HideUserInterface { get; set; } = true;
    public bool IsMouseAbsolute { get; set; }
    public int? EmulationSpeed { get; set; }
    public int? PhysicalMemorySize { get; set; }
    public MidiEngine? MidiEngine { get; set; }
    public string? Title { get; set; }
    public Dictionary<string, HeadlessDrive> Drives { get; } = new(StringComparer.OrdinalIgnoreCase);

    public AeonConfiguration ToAeonConfiguration()
    {
        var cfg = new AeonConfiguration
        {
            StartupPath = StartupPath,
            Launch = Launch ?? string.Empty,
            HideUserInterface = HideUserInterface,
            IsMouseAbsolute = IsMouseAbsolute,
            EmulationSpeed = EmulationSpeed,
            PhysicalMemorySize = PhysicalMemorySize,
            MidiEngine = MidiEngine,
            Title = Title ?? string.Empty
        };

        foreach (var (letter, drive) in Drives)
        {
            cfg.Drives[letter] = new AeonDriveConfiguration
            {
                Type = drive.Type,
                HostPath = drive.HostPath ?? string.Empty,
                ReadOnly = drive.ReadOnly,
                ImagePath = drive.ImagePath ?? string.Empty,
                FreeSpace = drive.FreeSpace,
                Label = drive.Label ?? string.Empty
            };
        }

        return cfg;
    }
}

public sealed class HeadlessDrive
{
    public Aeon.Emulator.DriveType Type { get; set; } = Aeon.Emulator.DriveType.Fixed;
    public string HostPath { get; set; } = string.Empty;
    public bool ReadOnly { get; set; }
    public string? ImagePath { get; set; }
    public long? FreeSpace { get; set; }
    public string? Label { get; set; }
}

/// <summary>
/// Mirrors writes to the real console stream and emits completed lines.
/// </summary>
internal sealed class ConsoleTapStream(Stream inner) : Stream
{
    private readonly Stream _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    private readonly StringBuilder _buffer = new();
    public event Action<string>? LineEmitted;

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => 0;
    public override long Position { get => 0; set => throw new NotSupportedException(); }

    public override void Flush() => _inner.Flush();
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
    public override void Write(ReadOnlySpan<byte> buffer)
    {
        _inner.Write(buffer);
        var text = Encoding.ASCII.GetString(buffer);
        AppendAndEmit(text);
    }
    public override void WriteByte(byte value)
    {
        _inner.WriteByte(value);
        AppendAndEmit(Encoding.ASCII.GetString([value]));
    }

    private void AppendAndEmit(string text)
    {
        foreach (var ch in text)
        {
            if (ch == '\r')
                continue;
            if (ch == '\n')
            {
                var line = _buffer.ToString();
                _buffer.Clear();
                LineEmitted?.Invoke(line);
            }
            else
            {
                _buffer.Append(ch);
            }
        }
    }
}

#pragma warning restore CA1416 // Validate platform compatibility

