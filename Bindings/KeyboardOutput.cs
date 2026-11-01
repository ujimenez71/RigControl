using System.Runtime.InteropServices;

namespace RigProbe.Bindings;

/// <summary>
/// Teclado virtual con SendInput por scancode. Las teclas llevan cuenta de referencias, así que dos
/// acciones que comparten CTRL no se lo sueltan una a la otra. Va a la ventana que tenga el foco
/// (en VR, el simulador debe tenerlo). Sin asignaciones en Tap/Down/Up/Tick.
/// </summary>
public sealed class KeyboardOutput : IDisposable
{
    /// <summary>Lo que dura pulsada una tecla en un toque: con 0 ms los simuladores pueden no verla.</summary>
    public const int TapMs = 50;

    private const int MaxPending = 64;

    // Índice: scancode | 0x100 si es extendida.
    private readonly int[] _refCount = new int[0x200];
    private readonly Input[] _buffer = new Input[8];

    private readonly KeyChord?[] _pendingChord = new KeyChord?[MaxPending];
    private readonly long[] _pendingAt = new long[MaxPending];

    /// <summary>Pulsa ahora y suelta a los TapMs (en Tick).</summary>
    public void Tap(KeyChord chord, long timeMs)
    {
        Down(chord);
        for (var i = 0; i < MaxPending; i++)
        {
            if (_pendingChord[i] is not null) continue;
            _pendingChord[i] = chord;
            _pendingAt[i] = timeMs + TapMs;
            return;
        }
        Up(chord); // cola llena (no debería pasar): mejor un toque corto que una tecla enganchada
    }

    public void Tick(long timeMs)
    {
        for (var i = 0; i < MaxPending; i++)
        {
            var chord = _pendingChord[i];
            if (chord is null || timeMs < _pendingAt[i]) continue;
            _pendingChord[i] = null;
            Up(chord);
        }
    }

    /// <summary>Modificadores primero, la tecla principal al final.</summary>
    public void Down(KeyChord chord)
    {
        var n = 0;
        for (var i = 0; i < chord.ScanCodes.Length; i++)
            if (_refCount[Index(chord, i)]++ == 0)
                _buffer[n++] = Make(chord.ScanCodes[i], chord.Extended[i], up: false);
        Send(n);
    }

    /// <summary>En orden inverso: la tecla principal primero y los modificadores después.</summary>
    public void Up(KeyChord chord)
    {
        var n = 0;
        for (var i = chord.ScanCodes.Length - 1; i >= 0; i--)
        {
            var k = Index(chord, i);
            if (_refCount[k] == 0) continue;
            if (--_refCount[k] == 0)
                _buffer[n++] = Make(chord.ScanCodes[i], chord.Extended[i], up: true);
        }
        Send(n);
    }

    /// <summary>Suelta todo lo que siga pulsado: nunca dejar una tecla enganchada al salir.</summary>
    public void ReleaseAll()
    {
        Array.Clear(_pendingChord);
        for (var k = 0; k < _refCount.Length; k++)
        {
            if (_refCount[k] == 0) continue;
            _refCount[k] = 0;
            _buffer[0] = Make((ushort)(k & 0xFF), (k & 0x100) != 0, up: true);
            Send(1);
        }
    }

    public void Dispose() => ReleaseAll();

    private static int Index(KeyChord chord, int i) => chord.ScanCodes[i] & 0xFF | (chord.Extended[i] ? 0x100 : 0);

    private static Input Make(ushort scan, bool extended, bool up) => new()
    {
        Type = 1, // INPUT_KEYBOARD
        Ki = new KeybdInput
        {
            Scan = scan,
            Flags = 0x0008 /* SCANCODE */ | (extended ? 0x0001u /* EXTENDEDKEY */ : 0) | (up ? 0x0002u /* KEYUP */ : 0),
        },
    };

    private void Send(int count)
    {
        if (count > 0) SendInput((uint)count, _buffer, InputSize);
    }

    private static readonly int InputSize = Marshal.SizeOf<Input>();

    // ---------------------------------------------------------------- Win32

    [StructLayout(LayoutKind.Sequential)]
    private struct KeybdInput
    {
        public ushort Vk;
        public ushort Scan;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int Dx, Dy;
        public uint MouseData, Flags, Time;
        public IntPtr ExtraInfo;
    }

    /// <summary>INPUT de Win32: el tamaño de la unión lo marca MOUSEINPUT, el miembro más grande.</summary>
    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MouseInput Mi;
        [FieldOffset(0)] public KeybdInput Ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public InputUnion U;

        public KeybdInput Ki { set => U.Ki = value; }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, [In] Input[] pInputs, int cbSize);
}
