using HapticSeat.Profiles;

namespace HapticSeat.Tests;

public sealed class ProfileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "HapticSeatTests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Theory]
    [InlineData("Bf 109*", "Bf 109 G-6", true)]
    [InlineData("bf 109*", "Bf 109 G-6", true)]
    [InlineData("Bf 109*", "Bf 110 E-2", false)]
    [InlineData("P-51D-?5", "P-51D-15", true)]
    [InlineData("Spitfire Mk.IXe", "Spitfire Mk.IXe", true)]
    [InlineData("Spitfire Mk.IXe", "Spitfire Mk.IXeX", false)] // el punto no es comodín
    public void Patrones_de_avion(string pattern, string aircraft, bool matches)
    {
        var profile = new Profile { Name = "x", Aircraft = [pattern] };
        Assert.Equal(matches, profile.Matches(aircraft));
    }

    [Fact]
    public void Eleccion_por_avion_con_General_de_reserva()
    {
        List<Profile> profiles =
        [
            new() { Name = Profile.DefaultName },
            new() { Name = "Messerschmitt", Aircraft = ["Bf 109*", "Bf 110*"] },
            new() { Name = "Cazas USA", Aircraft = ["P-51*", "P-47*"] },
        ];

        Assert.Equal("Messerschmitt", ProfileStore.ForAircraft(profiles, "Bf 110 G-2").Name);
        Assert.Equal("Cazas USA", ProfileStore.ForAircraft(profiles, "P-47D-28").Name);
        Assert.Equal(Profile.DefaultName, ProfileStore.ForAircraft(profiles, "Yak-9").Name);
    }

    [Fact]
    public void Guardar_y_cargar()
    {
        var store = new ProfileStore(_dir);
        var all = store.LoadAll();
        Assert.Single(all);
        Assert.True(all[0].IsDefault);

        var p = new Profile { Name = "Ju 87", Aircraft = ["Ju 87*"], MasterGain = 140 };
        p.For("guns").Gain = 180;
        p.For("engine").Enabled = false;
        store.Save(p);

        var loaded = new ProfileStore(_dir).LoadAll().Single(x => x.Name == "Ju 87");
        Assert.Equal(140, loaded.MasterGain);
        Assert.Equal(180, loaded.For("guns").Gain);
        Assert.False(loaded.For("engine").Enabled);
        Assert.True(loaded.For("buffet").Enabled); // los que faltan usan los valores por defecto
        Assert.Equal(100, loaded.For("buffet").Gain);
    }

    [Fact]
    public void Json_roto_se_ignora_con_aviso()
    {
        var store = new ProfileStore(_dir);
        store.LoadAll();
        File.WriteAllText(Path.Combine(store.ProfilesDir, "roto.json"), "{ esto no es json");

        var warnings = new List<string>();
        var all = store.LoadAll(warnings.Add);
        Assert.Single(all);
        Assert.Single(warnings);
    }

    [Fact]
    public void Json_editado_a_mano_con_comentarios()
    {
        var store = new ProfileStore(_dir);
        Directory.CreateDirectory(store.ProfilesDir);
        File.WriteAllText(Path.Combine(store.ProfilesDir, "a mano.json"), """
            {
              // perfil escrito a mano
              "name": "A mano",
              "aircraft": ["Il-2*"],
              "effects": { "hits": { "gain": 150 } },
            }
            """);

        var p = store.LoadAll().Single(x => x.Name == "A mano");
        Assert.Equal(150, p.For("hits").Gain);
        Assert.True(p.For("hits").Enabled);
    }

    [Fact]
    public void Ajustes_y_niveles_minimos()
    {
        var store = new ProfileStore(_dir);
        var settings = store.LoadSettings();
        Assert.Equal([20, 20, 20, 20, 25, 25, 30, 30], settings.MinLevelBytes());

        settings.MinLevels = [10, 10, 300, -5]; // corto y fuera de rango
        settings.AutoProfile = false;
        store.SaveSettings(settings);

        var loaded = new ProfileStore(_dir).LoadSettings();
        Assert.False(loaded.AutoProfile);
        Assert.Equal([10, 10, 254, 0, 25, 25, 30, 30], loaded.MinLevelBytes());
    }

    [Theory]
    [InlineData("Bf 109 G-6", true)]
    [InlineData("", false)]
    [InlineData("a/b", false)]
    [InlineData("¿qué?", false)]
    public void Nombres_validos(string name, bool valid) => Assert.Equal(valid, ProfileStore.IsValidName(name));
}
