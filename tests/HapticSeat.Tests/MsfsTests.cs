using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using HapticSeat.Effects;
using HapticSeat.Telemetry;
using HapticSeat.Telemetry.Msfs;

namespace HapticSeat.Tests;

public class MsfsTests
{
    private static int Index(string datum) => Array.FindIndex(MsfsVars.Datums, d => d.Name == datum);

    /// <summary>Un C172 en crucero: sin nada raro, para modificar solo lo que prueba cada caso.</summary>
    private static double[] Cruise()
    {
        var d = new double[MsfsVars.Count];
        d[Index("AIRSPEED INDICATED")] = 100;
        d[Index("PLANE ALT ABOVE GROUND")] = 1000;
        d[Index("G FORCE")] = 1;
        d[Index("INCIDENCE ALPHA")] = 2;
        d[Index("STALL ALPHA")] = 16;
        d[Index("NUMBER OF ENGINES")] = 1;
        d[Index("MAX RATED ENGINE RPM")] = 2700;
        d[Index("GENERAL ENG RPM:1")] = 2400;
        d[Index("GEAR CENTER POSITION")] = d[Index("GEAR LEFT POSITION")] = d[Index("GEAR RIGHT POSITION")] = 1;
        return d;
    }

    private static FlightState Apply(double[] d)
    {
        var s = new FlightState { Connected = true };
        MsfsVars.Apply(d, s);
        return s;
    }

    [Fact]
    public void Convierte_unidades_en_crucero()
    {
        var s = Apply(Cruise());
        Assert.Equal(51.44f, s.IasMs, 0.01f);
        Assert.Equal(1000, s.AglM);
        Assert.Equal(1, s.VerticalG, 0.001f);
        Assert.Equal(1, s.EngineCount);
        Assert.Equal(2400, s.EngineRpm[0]);
        Assert.Equal(2700, s.EngineMaxRpm[0]);
        Assert.Equal(0, s.BuffetAmplitude);
        Assert.All(s.WheelLoad, v => Assert.Equal(0, v));
    }

    [Fact]
    public void Turbina_usa_N1_como_proporcion()
    {
        var d = Cruise();
        d[Index("GENERAL ENG RPM:1")] = 0;
        d[Index("TURB ENG N1:1")] = 50;
        var s = Apply(d);
        Assert.Equal(0.5f, s.EngineRpm[0] / s.EngineMaxRpm[0], 0.001f);
    }

    [Fact]
    public void Motor_parado_no_da_rpm_y_los_de_mas_se_ignoran()
    {
        var d = Cruise();
        d[Index("GENERAL ENG RPM:1")] = 0;
        d[Index("GENERAL ENG RPM:2")] = 2000; // no existe: el avión tiene un solo motor
        var s = Apply(d);
        Assert.Equal(0, s.EngineRpm[0]);
        Assert.Equal(0, s.EngineRpm[1]);
    }

    [Fact]
    public void Numero_de_motores_absurdo_no_rompe()
    {
        var d = Cruise();
        d[Index("NUMBER OF ENGINES")] = 12;
        Assert.Equal(FlightState.MaxEngines, Apply(d).EngineCount);
        d[Index("NUMBER OF ENGINES")] = -1;
        Assert.Equal(0, Apply(d).EngineCount);
    }

    [Fact]
    public void Bimotor_reparte_los_motores_a_cada_lado()
    {
        var d = Cruise();
        d[Index("NUMBER OF ENGINES")] = 2;
        var s = Apply(d);
        Assert.True(s.EnginePos[0].Z < 0 && s.EnginePos[1].Z > 0);
    }

    [Fact]
    public void Buffet_crece_al_acercarse_a_la_perdida_y_no_existe_en_tierra()
    {
        var d = Cruise();
        d[Index("INCIDENCE ALPHA")] = 14.5;
        var near = Apply(d).BuffetAmplitude;
        d[Index("INCIDENCE ALPHA")] = 16;
        var full = Apply(d).BuffetAmplitude;
        Assert.InRange(near, 0.01f, full - 0.01f);

        d[Index("SIM ON GROUND")] = 1;
        Assert.Equal(0, Apply(d).BuffetAmplitude);
    }

    [Fact]
    public void Aviso_de_perdida_basta_para_que_haya_buffet()
    {
        var d = Cruise();
        d[Index("STALL ALPHA")] = 0; // avión sin el dato
        d[Index("STALL WARNING")] = 1;
        Assert.True(Apply(d).BuffetAmplitude > 0);
    }

    [Fact]
    public void Carga_en_ruedas_por_compresion_o_por_estar_en_el_suelo()
    {
        var d = Cruise();
        d[Index("SIM ON GROUND")] = 1;
        d[Index("CONTACT POINT COMPRESSION:1")] = 0.4;
        var s = Apply(d);
        Assert.Equal(0, s.WheelLoad[0]);
        Assert.Equal(0.4f, s.WheelLoad[1], 0.001f);

        d[Index("CONTACT POINT COMPRESSION:1")] = 0; // avión sin dato de compresión
        Assert.All(Apply(d).WheelLoad.Take(3), v => Assert.True(v > 0.1f));
    }

    [Fact]
    public void Tomar_tierra_da_el_golpe_de_contacto()
    {
        var engine = new EffectEngine(new GroundEffect());
        var motors = new float[8];
        var d = Cruise();
        engine.Update(Apply(d), [], 0.01f, motors);
        Assert.All(motors, v => Assert.Equal(0, v));

        d[Index("SIM ON GROUND")] = 1;
        d[Index("CONTACT POINT COMPRESSION:1")] = d[Index("CONTACT POINT COMPRESSION:2")] = 0.5;
        engine.Update(Apply(d), [], 0.01f, motors);
        Assert.True(motors.Max() > 0.3f);
    }

    [Fact]
    public void Avion_parado_en_tierra_no_vibra_por_rodadura()
    {
        var engine = new EffectEngine(new GroundEffect());
        var motors = new float[8];
        var d = Cruise();
        d[Index("AIRSPEED INDICATED")] = 0;
        d[Index("SIM ON GROUND")] = 1;
        for (var i = 0; i < 20; i++) engine.Update(Apply(d), [], 0.01f, motors);
        Assert.All(motors, v => Assert.Equal(0, v));
    }

    // ------------------------------------------------------ mensajes de SimConnect

    private static void WithMessage(int id, Action<IntPtr> fill, int size, Action<IntPtr> use)
    {
        var p = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.Copy(new byte[size], 0, p, size);
            Marshal.WriteInt32(p, 0, size);
            Marshal.WriteInt32(p, 8, id);
            fill(p);
            use(p);
        }
        finally
        {
            Marshal.FreeHGlobal(p);
        }
    }

    private static void SendSimEvent(MsfsSource source, uint eventId, int data) =>
        WithMessage(4, p => { Marshal.WriteInt32(p, 16, (int)eventId); Marshal.WriteInt32(p, 20, data); }, 28, p => source.Handle(p, 28));

    private static void SendFlight(MsfsSource source, double[] sample, int? declaredSize = null)
    {
        var size = 40 + sample.Length * sizeof(double);
        WithMessage(8, p =>
        {
            Marshal.WriteInt32(p, 12, 1);
            Marshal.Copy(sample, 0, p + 40, sample.Length);
        }, size, p => source.Handle(p, declaredSize ?? size));
    }

    [Fact]
    public void Los_datos_llegan_al_estado_y_se_cuentan()
    {
        using var source = new MsfsSource();
        SendFlight(source, Cruise());
        var s = new FlightState();
        source.Snapshot(s);
        Assert.Equal(51.44f, s.IasMs, 0.01f);
        Assert.Equal(1, source.PacketCount);
        Assert.Equal("MSFS", s.Source);
    }

    [Fact]
    public void Un_mensaje_corto_se_ignora()
    {
        using var source = new MsfsSource();
        SendFlight(source, Cruise(), declaredSize: 48);
        Assert.Equal(0, source.PacketCount);
    }

    [Fact]
    public void Sin_vuelo_en_marcha_cuenta_como_pausa_y_al_dejar_de_llegar_datos_tambien()
    {
        using var source = new MsfsSource();
        var s = new FlightState();

        SendFlight(source, Cruise());
        source.Snapshot(s);
        Assert.True(s.Paused); // aún no ha dicho que haya un vuelo

        SendSimEvent(source, 1, 1); // Sim = 1
        SendFlight(source, Cruise());
        source.Snapshot(s);
        Assert.False(s.Paused);

        SendSimEvent(source, 2, 1); // Pause = 1
        source.Snapshot(s);
        Assert.True(s.Paused);

        SendSimEvent(source, 2, 0);
        SendFlight(source, Cruise());
        source.Snapshot(s);
        Assert.False(s.Paused);

        Thread.Sleep(600); // MSFS deja de enviar en el menú de ESC
        source.Snapshot(s);
        Assert.True(s.Paused);
    }

    [Fact]
    public void Lee_el_titulo_del_avion()
    {
        using var source = new MsfsSource();
        var title = Encoding.ASCII.GetBytes("Cessna Skyhawk G1000\0");
        WithMessage(8, p =>
        {
            Marshal.WriteInt32(p, 12, 2);
            Marshal.Copy(title, 0, p + 40, title.Length);
        }, 40 + 256, p => source.Handle(p, 40 + 256));

        var s = new FlightState();
        source.Snapshot(s);
        Assert.Equal("Cessna Skyhawk G1000", s.Aircraft);
    }

    [Fact]
    public void Una_excepcion_del_simulador_queda_como_aviso()
    {
        using var source = new MsfsSource();
        WithMessage(1, p => { Marshal.WriteInt32(p, 12, 7); Marshal.WriteInt32(p, 16, 99); }, 24, p => source.Handle(p, 24));
        Assert.True(source.TryTakeNotice(out var notice));
        Assert.Contains("excepción 7", notice);
    }

    [Fact]
    public void Las_variables_no_se_repiten()
    {
        Assert.Equal(MsfsVars.Count, MsfsVars.Datums.Select(d => d.Name).Distinct().Count());
        Assert.All(MsfsVars.Datums, d => Assert.False(string.IsNullOrWhiteSpace(d.Unit)));
        // El orden importa: motores y ruedas se leen por posición
        Assert.Equal(Index("GENERAL ENG RPM:1") + 3, Index("GENERAL ENG RPM:4"));
        Assert.Equal(Index("TURB ENG N1:1") + 3, Index("TURB ENG N1:4"));
        Assert.Equal(Index("CONTACT POINT COMPRESSION:0") + 2, Index("CONTACT POINT COMPRESSION:2"));
    }

    // ------------------------------------------------------ varios simuladores

    private sealed class FakeSource(string name, bool connected, bool paused, float ias = 0) : ITelemetrySource
    {
        public string Name => name;
        public long PacketCount => 5;
        public bool Started;
        public Exception? StartError;
        public void Start() { Started = true; if (StartError is not null) throw StartError; }
        public void Snapshot(FlightState t) { t.Source = name; t.Connected = connected; t.Paused = paused; t.IasMs = ias; }
        public void DrainEvents(List<FlightEvent> into) => into.Add(new FlightEvent(FlightEventType.Hit, 1, Vector3.Zero));
        public bool TryTakeNotice(out string notice) { notice = name; return true; }
        public void Dispose() { }
    }

    private static string Chosen(params ITelemetrySource[] sources)
    {
        var s = new FlightState();
        new MultiSource(sources).Snapshot(s);
        return s.Source;
    }

    [Fact]
    public void Multi_elige_el_simulador_que_esta_volando()
    {
        var idle = new FakeSource("A", false, false);
        var menu = new FakeSource("B", true, true);
        var flying = new FakeSource("C", true, false);
        Assert.Equal("C", Chosen(idle, menu, flying));
        Assert.Equal("B", Chosen(idle, menu));
        Assert.Equal("A", Chosen(idle, new FakeSource("Z", false, false)));
        Assert.Equal("C", Chosen(flying, new FakeSource("Y", true, false)));
    }

    [Fact]
    public void Multi_arranca_todos_aunque_uno_falle_y_avisa_al_final()
    {
        var bad = new FakeSource("A", false, false) { StartError = new System.Net.Sockets.SocketException(10048) };
        var good = new FakeSource("B", false, false);
        Assert.Throws<System.Net.Sockets.SocketException>(new MultiSource(bad, good).Start);
        Assert.True(good.Started);
    }

    [Fact]
    public void Multi_junta_eventos_y_paquetes()
    {
        using var multi = new MultiSource(new FakeSource("A", true, false), new FakeSource("B", true, false));
        var events = new List<FlightEvent>();
        multi.DrainEvents(events);
        Assert.Equal(2, events.Count);
        Assert.Equal(10, multi.PacketCount);
    }
}
