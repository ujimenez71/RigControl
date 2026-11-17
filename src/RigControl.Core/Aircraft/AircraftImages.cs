using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace RigControl.Core.Aircraft;

/// <summary>Autoría de una imagen descargada (se enseña en la tarjeta: las licencias CC lo exigen).</summary>
public sealed record ImageCredit(string Article, string File, string Author, string License, string? LicenseUrl, string Page, DateTime Downloaded)
{
    public string Text => $"{Author} · {License} · Wikimedia Commons";
}

/// <summary>
/// Fotos reales de las aeronaves de los perfiles (pantalla Perfiles): %APPDATA%\RigControl\aviones\&lt;perfil&gt;.jpg|png
/// (+ &lt;perfil&gt;.json con la autoría si se descargó). Si no hay, se busca: artículo de Wikipedia de la aeronave
/// (tabla de las habituales de DCS y MSFS, o búsqueda que exige el número de modelo en el título) -> su foto
/// principal, SOLO si está en Wikimedia Commons (libre: dominio público o CC) -> se guarda a 1280 px (tamaño estándar de
/// Wikimedia: otros los limita) con su
/// autoría. Un intento fallido deja &lt;perfil&gt;.sinimagen para no repetirlo solo (el botón "Buscar otra" lo ignora).
/// Solo se manda a Wikipedia el nombre de la aeronave. HttpMessageHandler sustituible para las pruebas.
/// </summary>
public sealed partial class AircraftImages(string? directory = null, HttpMessageHandler? handler = null)
{
    public string Directory { get; } = directory ?? Path.Combine(Path.GetDirectoryName(DeviceCatalog.DefaultPath)!, "aviones");

    private static readonly string[] Extensions = [".jpg", ".jpeg", ".png", ".webp"];

    private readonly HttpClient _http = CreateClient(handler);

    private static HttpClient CreateClient(HttpMessageHandler? handler)
    {
        var http = handler is null ? new HttpClient() : new HttpClient(handler);
        http.Timeout = TimeSpan.FromSeconds(30);
        // Wikimedia pide identificar a los clientes automáticos; nombre genérico, sin datos del usuario.
        http.DefaultRequestHeaders.UserAgent.ParseAdd("RigControl/1.0 (aircraft card images; desktop app)");
        return http;
    }

    /// <summary>Artículos de Wikipedia (inglés) de las aeronaves habituales: trozo del nombre en DCS/MSFS -> título.</summary>
    public static readonly (string Token, string Title)[] KnownArticles =
    [
        ("ah-64", "Boeing AH-64 Apache"), ("ah64", "Boeing AH-64 Apache"), ("apache", "Boeing AH-64 Apache"),
        ("uh-1", "Bell UH-1 Iroquois"), ("uh1", "Bell UH-1 Iroquois"), ("huey", "Bell UH-1 Iroquois"),
        ("uh-60", "Sikorsky UH-60 Black Hawk"), ("ch-47", "Boeing CH-47 Chinook"), ("oh-58", "Bell OH-58 Kiowa"),
        ("ah-6", "MD Helicopters MH-6 Little Bird"), ("ka-50", "Kamov Ka-50"), ("mi-8", "Mil Mi-8"), ("mi-24", "Mil Mi-24"),
        ("sa342", "Aérospatiale Gazelle"), ("f-16", "General Dynamics F-16 Fighting Falcon"), ("f16", "General Dynamics F-16 Fighting Falcon"),
        ("fa-18", "McDonnell Douglas F/A-18 Hornet"), ("f/a-18", "McDonnell Douglas F/A-18 Hornet"), ("f-18", "McDonnell Douglas F/A-18 Hornet"),
        ("f-15e", "McDonnell Douglas F-15E Strike Eagle"), ("f-15", "McDonnell Douglas F-15 Eagle"),
        ("f-14", "Grumman F-14 Tomcat"), ("f-4", "McDonnell Douglas F-4 Phantom II"), ("f-5", "Northrop F-5"),
        ("f-86", "North American F-86 Sabre"), ("a-10", "Fairchild Republic A-10 Thunderbolt II"), ("a-4", "Douglas A-4 Skyhawk"),
        ("av8b", "McDonnell Douglas AV-8B Harrier II"), ("av-8b", "McDonnell Douglas AV-8B Harrier II"),
        ("m-2000", "Dassault Mirage 2000"), ("mirage 2000", "Dassault Mirage 2000"), ("mirage-f1", "Dassault Mirage F1"),
        ("mig-29", "Mikoyan MiG-29"), ("mig-21", "Mikoyan-Gurevich MiG-21"), ("mig-19", "Mikoyan-Gurevich MiG-19"),
        ("mig-15", "Mikoyan-Gurevich MiG-15"), ("su-27", "Sukhoi Su-27"), ("su-33", "Sukhoi Su-33"), ("su-25", "Sukhoi Su-25"),
        ("jf-17", "CAC/PAC JF-17 Thunder"), ("ajs37", "Saab 37 Viggen"), ("viggen", "Saab 37 Viggen"),
        ("l-39", "Aero L-39 Albatros"), ("c-101", "CASA C-101 Aviojet"), ("mb-339", "Aermacchi MB-339"), ("yak-52", "Yakovlev Yak-52"),
        ("p-51", "North American P-51 Mustang"), ("tf-51", "North American P-51 Mustang"), ("p-47", "Republic P-47 Thunderbolt"),
        ("spitfire", "Supermarine Spitfire"), ("bf-109", "Messerschmitt Bf 109"), ("fw-190", "Focke-Wulf Fw 190"),
        ("mosquito", "de Havilland Mosquito"), ("f4u", "Vought F4U Corsair"), ("corsair", "Vought F4U Corsair"),
        ("c172", "Cessna 172"), ("c152", "Cessna 152"), ("c208", "Cessna 208 Caravan"), ("caravan", "Cessna 208 Caravan"),
        ("c310", "Cessna 310"), ("da40", "Diamond DA40 Diamond Star"), ("da62", "Diamond DA62"), ("tbm", "Daher TBM"),
        ("king air", "Beechcraft King Air"), ("kingair", "Beechcraft King Air"), ("bonanza", "Beechcraft Bonanza"),
        ("baron", "Beechcraft Baron"), ("pc-6", "Pilatus PC-6 Porter"), ("pc6", "Pilatus PC-6 Porter"), ("pc-12", "Pilatus PC-12"),
        ("kodiak", "Quest Kodiak"), ("cub", "Piper J-3 Cub"), ("savage", "Zlin Savage"), ("dhc-2", "de Havilland Canada DHC-2 Beaver"),
        ("beaver", "de Havilland Canada DHC-2 Beaver"), ("dhc-6", "de Havilland Canada DHC-6 Twin Otter"),
        ("a320", "Airbus A320 family"), ("a310", "Airbus A310"), ("737", "Boeing 737"), ("747", "Boeing 747"), ("787", "Boeing 787 Dreamliner"),
        ("h135", "Eurocopter EC135"), ("ec135", "Eurocopter EC135"), ("h125", "Eurocopter AS350 Écureuil"), ("h145", "Airbus Helicopters H145"),
        ("r44", "Robinson R44"), ("r66", "Robinson R66"), ("cabri", "Guimbal Cabri G2"), ("bell 407", "Bell 407"),
    ];

    // ------------------------------------------------------------------ local

    /// <summary>Imagen ya guardada del perfil (por el nombre de su archivo o de uno de sus "aircraft"), o null.</summary>
    public string? Find(string profileBase, IEnumerable<string> aircraft)
    {
        if (!System.IO.Directory.Exists(Directory)) return null;
        foreach (var n in new[] { profileBase }.Concat(aircraft.Where(a => a != "*").Select(Clean)))
            foreach (var ext in Extensions)
            {
                var f = Path.Combine(Directory, n + ext);
                if (File.Exists(f)) return f;
            }
        return null;
    }

    public ImageCredit? Credit(string imagePath)
    {
        var json = Path.ChangeExtension(imagePath, ".json");
        if (!File.Exists(json)) return null;
        try { return JsonSerializer.Deserialize<ImageCredit>(File.ReadAllText(json)); }
        catch (JsonException) { return null; }
    }

    public bool TriedWithoutLuck(string profileBase) => File.Exists(Path.Combine(Directory, profileBase + ".sinimagen"));

    /// <summary>Copia una imagen elegida por el usuario como la del perfil (quita la anterior y su autoría).</summary>
    public string UseOwn(string profileBase, string sourceFile)
    {
        System.IO.Directory.CreateDirectory(Directory);
        RemoveExisting(profileBase);
        var dest = Path.Combine(Directory, profileBase + Path.GetExtension(sourceFile).ToLowerInvariant());
        File.Copy(sourceFile, dest, overwrite: true);
        return dest;
    }

    private void RemoveExisting(string profileBase)
    {
        foreach (var ext in Extensions.Append(".json").Append(".sinimagen"))
        {
            var f = Path.Combine(Directory, profileBase + ext);
            if (File.Exists(f)) File.Delete(f);
        }
    }

    // ------------------------------------------------------------------ búsqueda

    /// <summary>"dcs:AH-64D_BLK_II" -> "AH-64D_BLK_II"; para nombres de archivo.</summary>
    private static string Clean(string aircraft) =>
        aircraft.StartsWith("dcs:", StringComparison.OrdinalIgnoreCase) ? aircraft[4..] : aircraft;

    [GeneratedRegex(@"[a-z]{1,4}[- ]?\d{1,4}", RegexOptions.IgnoreCase)]
    private static partial Regex ModelRegex();

    /// <summary>Texto de búsqueda: nombre del perfil y de las aeronaves sin prefijos, sufijos de versión ni "(DCS)".</summary>
    public static string SearchText(string profileName, IEnumerable<string> aircraft)
    {
        var parts = new[] { profileName }.Concat(aircraft.Where(a => a != "*").Select(Clean)).Select(p =>
        {
            p = p.Replace('_', ' ');
            p = Regex.Replace(p, @"\(.*?\)", " ");
            p = Regex.Replace(p, @"\b(asobo|microsoft|blk|ii|iii|dcs|msfs|g1000|g3000|sp|vr|mod)\b", " ", RegexOptions.IgnoreCase);
            return Regex.Replace(p, @"\s+", " ").Trim();
        });
        return string.Join(' ', parts.Where(p => p.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>Números de modelo del texto ("AH-64", "172"): el artículo elegido debe llevar alguno en el título.</summary>
    public static List<string> ModelNumbers(string text) =>
        [.. ModelRegex().Matches(text).Select(m => Regex.Match(m.Value, @"\d+").Value).Where(n => n.Length > 0).Distinct()];

    /// <summary>Artículo de la tabla para el perfil, o null.</summary>
    public static string? KnownArticle(string profileName, IEnumerable<string> aircraft)
    {
        var text = (profileName + " " + string.Join(' ', aircraft.Select(Clean))).ToLowerInvariant().Replace('_', ' ');
        return KnownArticles.FirstOrDefault(k => text.Contains(k.Token)).Title;
    }

    /// <summary>
    /// Busca y guarda la foto del perfil. skip = cuántas candidatas saltarse ("Buscar otra"). Devuelve la ruta o null
    /// (y deja la marca .sinimagen). No lanza por red: sin conexión = null.
    /// </summary>
    public async Task<string?> FetchAsync(string profileBase, string profileName, IReadOnlyList<string> aircraft, int skip = 0,
                                          CancellationToken cancel = default)
    {
        try
        {
            var candidates = new List<string>();
            if (KnownArticle(profileName, aircraft) is { } known) candidates.Add(known);
            var search = SearchText(profileName, aircraft);
            var models = ModelNumbers(search);
            foreach (var t in await SearchAsync(search + " aircraft", cancel))
                if (!candidates.Contains(t) && (models.Count == 0 || models.Any(m => t.Contains(m, StringComparison.Ordinal))))
                    candidates.Add(t);

            // Fotos: la principal de cada artículo, y en "Buscar otra" también las demás del primero.
            var images = new List<(string Article, string File)>();
            foreach (var article in candidates.Take(3))
            {
                if (await LeadImageAsync(article, cancel) is { } lead) images.Add((article, lead));
                if (skip > 0 && article == candidates[0])
                    foreach (var f in await ArticleImagesAsync(article, cancel))
                        if (!images.Any(i => i.File == f)) images.Add((article, f));
            }
            foreach (var (article, file) in images.Skip(skip))
            {
                if (await CommonsInfoAsync(file, cancel) is not { } info) continue;   // no está en Commons = no libre
                var bytes = await GetBytes(info.Url, cancel);
                System.IO.Directory.CreateDirectory(Directory);
                RemoveExisting(profileBase);
                var ext = Path.GetExtension(new Uri(info.Url).AbsolutePath).ToLowerInvariant() switch { ".png" => ".png", ".webp" => ".webp", _ => ".jpg" };
                var dest = Path.Combine(Directory, profileBase + ext);
                await File.WriteAllBytesAsync(dest, bytes, cancel);
                var credit = new ImageCredit(article, file, info.Author, info.License, info.LicenseUrl, info.Page, DateTime.Now);
                await File.WriteAllTextAsync(Path.ChangeExtension(dest, ".json"), JsonSerializer.Serialize(credit, new JsonSerializerOptions { WriteIndented = true }), cancel);
                return dest;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or IOException)
        {
            if (cancel.IsCancellationRequested) throw;
            Log.Warning($"Imagen de «{profileName}»: no se pudo descargar ({ex.Message}).");
            return null;   // sin red: se reintentará la próxima vez (sin marca)
        }
        System.IO.Directory.CreateDirectory(Directory);
        await File.WriteAllTextAsync(Path.Combine(Directory, profileBase + ".sinimagen"), SearchText(profileName, aircraft), cancel);
        return null;
    }

    private async Task<JsonNode?> GetJson(string url, CancellationToken cancel) =>
        JsonNode.Parse(System.Text.Encoding.UTF8.GetString(await GetBytes(url, cancel)));

    /// <summary>GET con reintentos si Wikimedia contesta 429/503 (espera lo que diga Retry-After, o 2-8 s).</summary>
    private async Task<byte[]> GetBytes(string url, CancellationToken cancel)
    {
        for (var attempt = 1; ; attempt++)
        {
            using var response = await _http.GetAsync(url, cancel);
            if ((response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable) && attempt < 4)
            {
                var wait = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(2 * attempt * attempt);
                await Task.Delay(wait > TimeSpan.FromSeconds(30) ? TimeSpan.FromSeconds(30) : wait, cancel);
                continue;
            }
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsByteArrayAsync(cancel);
        }
    }

    private async Task<List<string>> SearchAsync(string text, CancellationToken cancel)
    {
        var j = await GetJson($"https://en.wikipedia.org/w/api.php?action=query&list=search&format=json&srlimit=8&srsearch={WebUtility.UrlEncode(text)}", cancel);
        return [.. (j?["query"]?["search"]?.AsArray() ?? []).Select(r => r?["title"]?.GetValue<string>()).OfType<string>()];
    }

    private async Task<string?> LeadImageAsync(string article, CancellationToken cancel)
    {
        var j = await GetJson($"https://en.wikipedia.org/w/api.php?action=query&prop=pageimages&piprop=name&redirects=1&format=json&titles={WebUtility.UrlEncode(article)}", cancel);
        return (j?["query"]?["pages"]?.AsObject().Select(p => p.Value?["pageimage"]?.GetValue<string>()).FirstOrDefault(n => n is not null));
    }

    private async Task<List<string>> ArticleImagesAsync(string article, CancellationToken cancel)
    {
        var j = await GetJson($"https://en.wikipedia.org/w/api.php?action=query&prop=images&imlimit=50&redirects=1&format=json&titles={WebUtility.UrlEncode(article)}", cancel);
        return [.. (j?["query"]?["pages"]?.AsObject().SelectMany(p => p.Value?["images"]?.AsArray() ?? []) ?? [])
            .Select(i => i?["title"]?.GetValue<string>()).OfType<string>()
            .Select(t => t.StartsWith("File:", StringComparison.Ordinal) ? t[5..] : t)
            .Where(t => t.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || t.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase))];
    }

    private sealed record CommonsInfo(string Url, string Author, string License, string? LicenseUrl, string Page);

    private async Task<CommonsInfo?> CommonsInfoAsync(string file, CancellationToken cancel)
    {
        var j = await GetJson("https://commons.wikimedia.org/w/api.php?action=query&prop=imageinfo&iiprop=url%7Cextmetadata&iiurlwidth=1280&format=json&titles="
                              + WebUtility.UrlEncode("File:" + file.Replace(' ', '_')), cancel);
        var page = j?["query"]?["pages"]?.AsObject().Select(p => p.Value).FirstOrDefault();
        if (page is null || page["missing"] is not null || page["imageinfo"] is not JsonArray infos || infos.Count == 0) return null;
        var ii = infos[0]!;
        var meta = ii["extmetadata"];
        string M(string key) => StripHtml(meta?[key]?["value"]?.GetValue<string>() ?? "");
        var url = ii["thumburl"]?.GetValue<string>() ?? ii["url"]?.GetValue<string>();
        if (url is null) return null;
        var author = M("Artist");
        return new CommonsInfo(url, author.Length == 0 ? "autor desconocido" : author, M("LicenseShortName") is { Length: > 0 } l ? l : "licencia libre",
                               M("LicenseUrl") is { Length: > 0 } lu ? lu : null, ii["descriptionurl"]?.GetValue<string>() ?? "");
    }

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex TagRegex();

    private static string StripHtml(string s) => WebUtility.HtmlDecode(TagRegex().Replace(s, "")).Trim().Trim('"').Trim();
}
