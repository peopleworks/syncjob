using System.Text;
using System.Text.Json;
using Spectre.Console;

namespace SyncJob.IntegrationTests;

/// <summary>
/// The CLI's <c>run</c> command, end to end, through its own entry point and against
/// two real databases.
/// <para>
/// Through <c>Program.RunAsCli</c> and not through <c>JobRunner</c>: the engine already
/// has live tests of its own, and what is new here is everything between the command
/// line and the engine - the section that gets loaded, the flags that get applied, the
/// guard's answer becoming an exit code. A test that called the runner directly would
/// pass while <c>--force-commit</c> was wired to nothing.
/// </para>
/// <para>
/// Two databases rather than two schemas in one, as everywhere else here. The
/// destinations come from <c>PublicationFixture</c>, so they carry the things a
/// hand-maintained staging table gets wrong - an identity, a computed column, a
/// rowversion, a default, a check constraint and a filtered index - and their rowversion
/// is what lets "the destination was not touched" be asserted row for row rather than
/// inferred from a count.
/// </para>
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class CliLiveTests : IDisposable
{
    private const string Seccion = "PruebaCli";
    private const string Tabla = "dbo.Ledger";

    private readonly SqlServerFixture _fixture;
    private readonly string _carpeta;

    public CliLiveTests(SqlServerFixture fixture)
    {
        _fixture = fixture;
        _carpeta = Path.Combine(Path.GetTempPath(), "syncjob-cli-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_carpeta);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_carpeta, recursive: true);
        }
        catch(IOException)
        {
            // A leftover temp directory is a nuisance, not a test failure.
        }
    }

    /// <summary>
    /// The ordinary night: a section loads and the destination ends up holding exactly
    /// what the source returned, row for row rather than by count. A publication that
    /// goes in shifted has the right count.
    /// </summary>
    [LiveFact]
    public async Task UnaSeccionCargaDePuntaAPuntaYElDestinoQuedaIgualAlOrigen()
    {
        var (origen, destino) = await CrearParAsync(filasOrigen: 500, filasDestino: 100);
        var archivo = EscribirConfig(origen, destino, minimoParaCommit: 1);

        var (codigo, salida) = Ejecutar("run", "-c", archivo, "-s", Seccion);

        Assert.Equal(0, codigo);
        Assert.Contains("Sync OK", salida);
        Assert.Equal(await FilasAsync(origen), await FilasAsync(destino));
        Assert.Equal(500, await SqlServerFixture.CountAsync(destino, Tabla));
    }

    /// <summary>
    /// The failure the guard exists for: a source that comes back empty, followed by a
    /// replace, is a job that empties a production table and reports success.
    /// <para>
    /// The destination is compared with its rowversions, so this asserts that the rows
    /// were not touched rather than that there are still the same number of them.
    /// </para>
    /// </summary>
    [LiveFact]
    public async Task UnOrigenQueNoDevuelveNadaEsRechazadoYElDestinoQuedaIntacto()
    {
        var (origen, destino) = await CrearParAsync(filasOrigen: 0, filasDestino: 300);
        var archivo = EscribirConfig(origen, destino, minimoParaCommit: 1);

        var antes = await FilasConVersionAsync(destino);

        var (codigo, salida) = Ejecutar("run", "-c", archivo, "-s", Seccion);

        Assert.Equal(1, codigo);
        Assert.Contains("the guard refused to publish", salida);
        Assert.Contains("0 staged, 1 required", salida);
        Assert.Equal(antes, await FilasConVersionAsync(destino));
    }

    /// <summary>
    /// <c>--force-commit</c> publishes over the guard's refusal and the run says so.
    /// Saying so is the half that matters: a forced run that reads like an ordinary one
    /// is how an override becomes permanent.
    /// </summary>
    [LiveFact]
    public async Task ForceCommitPublicaIgualYDejaConstanciaDeQueSeAnuloElSeguro()
    {
        var (origen, destino) = await CrearParAsync(filasOrigen: 0, filasDestino: 300);
        var archivo = EscribirConfig(origen, destino, minimoParaCommit: 1);

        var (codigo, salida) = Ejecutar("run", "-c", archivo, "-s", Seccion, "--force-commit");

        Assert.Equal(0, codigo);
        Assert.Contains("the guard refused and the step is set to publish anyway, so it was overridden", salida);
        Assert.Equal(0, await SqlServerFixture.CountAsync(destino, Tabla));
    }

    /// <summary>
    /// <c>--skip-commit</c> is the guard's <c>Skip</c>: the destination is left one load
    /// stale, the run carries on and the exit code is zero, because a skip is a report
    /// and not a phone call. What the run must not do is report it as a success.
    /// </summary>
    [LiveFact]
    public async Task SkipCommitDejaElDestinoComoEstabaYSaleConCero()
    {
        var (origen, destino) = await CrearParAsync(filasOrigen: 0, filasDestino: 300);
        var archivo = EscribirConfig(origen, destino, minimoParaCommit: 1);

        var antes = await FilasConVersionAsync(destino);

        var (codigo, salida) = Ejecutar("run", "-c", archivo, "-s", Seccion, "--skip-commit");

        Assert.Equal(0, codigo);
        Assert.Contains("Sin publicar", salida);
        Assert.DoesNotContain("Sync OK", salida);
        Assert.Equal(antes, await FilasConVersionAsync(destino));
    }

    /// <summary>
    /// <c>--dry-run</c> does considerably more than it used to: it stages the real
    /// source and runs the guard's arithmetic against real counts. So it has to report
    /// the counting rather than "Dry-run OK", and it still has to leave the destination
    /// exactly as it found it - rowversions included.
    /// </summary>
    [LiveFact]
    public async Task ElEnsayoCuentaDeVerdadYNoEscribeNada()
    {
        var (origen, destino) = await CrearParAsync(filasOrigen: 1_240, filasDestino: 300);
        var archivo = EscribirConfig(origen, destino, minimoParaCommit: 1);

        var antes = await FilasConVersionAsync(destino);

        var (codigo, salida) = Ejecutar("run", "-c", archivo, "-s", Seccion, "--dry-run");

        Assert.Equal(0, codigo);
        Assert.Contains("1,240 rows were staged from the real source", salida);
        Assert.Contains("the guard would have allowed it", salida);
        Assert.Contains("Nothing was written", salida);
        Assert.Equal(antes, await FilasConVersionAsync(destino));
    }

    /// <summary>
    /// <c>--init-tracking</c> leaves the engine's own watermark table behind and, above
    /// all, does not go on to load anything.
    /// <para>
    /// The second half is the point. The flag used to be honoured only when the section
    /// had <c>Incremental.Enabled</c>; on any other section it was ignored and the
    /// command carried straight on into a full production load, which is the last thing
    /// whoever typed <c>--init-tracking</c> was asking for.
    /// </para>
    /// </summary>
    [LiveFact]
    public async Task InitTrackingDejaLaTablaDeMarcasYNoCargaNada()
    {
        var (origen, destino) = await CrearParAsync(filasOrigen: 500, filasDestino: 100);
        var archivo = EscribirConfig(origen, destino, minimoParaCommit: 1);

        var antes = await FilasConVersionAsync(destino);

        var (codigo, salida) = Ejecutar("run", "-c", archivo, "-s", Seccion, "--init-tracking");

        // Lo primero que se comprueba es que no cargó: es la mitad peligrosa.
        Assert.Equal(antes, await FilasConVersionAsync(destino));
        Assert.Equal(100, await SqlServerFixture.CountAsync(destino, Tabla));
        Assert.Equal(0, codigo);
        Assert.Contains("dbo.SyncJobWatermark", salida);
        Assert.Equal(0, await SqlServerFixture.CountAsync(destino, "dbo.SyncJobWatermark"));
    }

    /// <summary>
    /// <c>--all</c> runs every section through the same path, and the flags on the
    /// command line reach every one of them.
    /// <para>
    /// The second run is the one worth having. <c>--all</c> used to rebuild a
    /// <c>RunSettings</c> per section field by field, and the copy left out
    /// <c>--min-commit</c>, <c>--top</c>, <c>--batch-size</c>, <c>--maxdop</c>,
    /// <c>--sp</c> and the certificate options: the operator typed a floor of ten and
    /// got the file's. Passing the section name instead of a copy makes that class of
    /// omission unwritable.
    /// </para>
    /// </summary>
    [LiveFact]
    public async Task AllCorreCadaSeccionPorLaMismaRutaYLesLlevaLasBanderas()
    {
        var (origen, destino) = await _fixture.CreatePairAsync();
        await PublicationFixture.CreateLedgerAsync(origen, "dbo.Uno");
        await PublicationFixture.CreateLedgerAsync(origen, "dbo.Dos");
        await PublicationFixture.CreateLedgerAsync(destino, "dbo.Uno");
        await PublicationFixture.CreateLedgerAsync(destino, "dbo.Dos");
        await PublicationFixture.LoadAsync(origen, "dbo.Uno", rows: 5, firstId: 1);
        await PublicationFixture.LoadAsync(origen, "dbo.Dos", rows: 5, firstId: 1);

        var archivo = Path.Combine(_carpeta, "todas.json");
        File.WriteAllText(archivo, DosSecciones(origen, destino), new UTF8Encoding(false));

        var (codigo, salida) = Ejecutar("run", "-c", archivo, "--all");

        Assert.Equal(0, codigo);
        Assert.Contains("2 secciones sincronizadas", salida);
        Assert.Equal(5, await SqlServerFixture.CountAsync(destino, "dbo.Uno"));
        Assert.Equal(5, await SqlServerFixture.CountAsync(destino, "dbo.Dos"));

        var antesUno = await FilasConVersionAsync(destino, "dbo.Uno");
        var antesDos = await FilasConVersionAsync(destino, "dbo.Dos");

        // Un piso de diez contra cinco filas: si --min-commit no llega a las secciones,
        // esto vuelve a pasar y las dos tablas se vuelven a escribir.
        var (rechazo, _) = Ejecutar("run", "-c", archivo, "--all", "--min-commit", "10", "--continue-on-error");

        Assert.Equal(1, rechazo);
        Assert.Equal(antesUno, await FilasConVersionAsync(destino, "dbo.Uno"));
        Assert.Equal(antesDos, await FilasConVersionAsync(destino, "dbo.Dos"));
    }

    /// <summary>
    /// <c>--append</c> adds its rows and leaves everything that was already there
    /// exactly as it was - rowversions included, which is what distinguishes an append
    /// from a replace that happens to have kept the same count.
    /// </summary>
    [LiveFact]
    public async Task AppendAgregaSusFilasYNoTocaLasQueYaEstaban()
    {
        var (origen, destino) = await _fixture.CreatePairAsync();
        await PublicationFixture.CreateLedgerAsync(origen, Tabla);
        await PublicationFixture.CreateLedgerAsync(destino, Tabla);

        // Los ids de lo que ya está van primero, para que "las cien primeras filas por
        // Id" sean las cien que ya estaban y no las nuevas.
        await PublicationFixture.LoadAsync(destino, Tabla, rows: 100, firstId: 1);
        await PublicationFixture.LoadAsync(origen, Tabla, rows: 50, firstId: 1_000);

        var archivo = EscribirConfig(origen, destino, minimoParaCommit: 1);
        var antes = await FilasConVersionAsync(destino);

        var (codigo, _) = Ejecutar("run", "-c", archivo, "-s", Seccion, "--append");

        Assert.Equal(0, codigo);

        var despues = await FilasConVersionAsync(destino);
        Assert.Equal(150, despues.Count);
        Assert.Equal(antes, despues.Take(100).ToList());
    }

    /// <summary>
    /// A section carrying the <c>Incremental</c> block exactly as INCREMENTAL_SYNC.md
    /// writes it - its enums spelled out - is one <c>validate</c> can load.
    /// <para>
    /// It could not, until now. <c>LoadConfig</c> registered no string-enum converter,
    /// so <c>"Mode": "Timestamp"</c> threw a raw <c>JsonException</c> out of
    /// deserialisation before any validation ran: every incremental section the
    /// documentation describes failed to load at all. Nothing about the engine; the file
    /// simply never got read.
    /// </para>
    /// </summary>
    [LiveFact]
    public async Task UnaSeccionIncrementalEscritaComoLaDocumentacionSeCarga()
    {
        var (origen, destino) = await CrearParAsync(filasOrigen: 10, filasDestino: 10);

        string json = $$"""
        {
          "{{Seccion}}": {
            "Source": {
              "ConnectionString": {{Texto(origen)}},
              "Query": "SELECT Id, Name, Amount, ChangedAt FROM {{Tabla}}"
            },
            "Destination": {
              "ConnectionString": {{Texto(destino)}},
              "StageTable": "dbo.Ledger_stage",
              "FinalTable": "{{Tabla}}"
            },
            "Options": {
              "BatchSize": 5000, "MaxDegreeOfParallelism": 1, "BulkCopyTimeoutSeconds": 0,
              "KeepIdentity": true, "MinRowThresholdToCommit": 1
            },
            "Incremental": {
              "Enabled": true,
              "Mode": "Timestamp",
              "TrackingColumn": "ChangedAt",
              "TrackingTable": "dbo.SyncJobTracking",
              "MergeStrategy": "Upsert",
              "PrimaryKeyColumns": [ "Id" ]
            }
          }
        }
        """;

        string archivo = Path.Combine(_carpeta, "incremental.json");
        File.WriteAllText(archivo, json, new UTF8Encoding(false));

        var (codigo, salida) = Ejecutar("validate", "-c", archivo, "-s", Seccion);

        Assert.DoesNotContain("JsonException", salida);
        Assert.Equal(0, codigo);
        Assert.Contains("Validación OK", salida);
    }

    // ------------------------------------------------------------------ montaje

    /// <summary>
    /// Runs the CLI exactly as a shell would and captures what it printed.
    /// <para>
    /// The console is swapped for one writing into a string, wide enough that a
    /// message is not wrapped into something no assertion can find; the text is then
    /// flattened, because where Spectre wraps a line is not part of what is being
    /// tested.
    /// </para>
    /// </summary>
    private static (int Codigo, string Salida) Ejecutar(params string[] args)
    {
        var escritor = new StringWriter();
        var anterior = AnsiConsole.Console;

        var consola = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Interactive = InteractionSupport.No,
            Out = new AnsiConsoleOutput(escritor)
        });

        consola.Profile.Width = 400;
        AnsiConsole.Console = consola;

        try
        {
            int codigo = global::SyncJob.Program.RunAsCli(args);
            return (codigo, Aplanar(escritor.ToString()));
        }
        finally
        {
            AnsiConsole.Console = anterior;
        }
    }

    private static string Aplanar(string texto) =>
        string.Join(' ', texto.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private async Task<(string Origen, string Destino)> CrearParAsync(int filasOrigen, int filasDestino)
    {
        var (origen, destino) = await _fixture.CreatePairAsync();

        await PublicationFixture.CreateLedgerAsync(origen, Tabla);
        await PublicationFixture.CreateLedgerAsync(destino, Tabla);

        if(filasOrigen > 0)
            await PublicationFixture.LoadAsync(origen, Tabla, filasOrigen, firstId: 1);

        if(filasDestino > 0)
            await PublicationFixture.LoadAsync(destino, Tabla, filasDestino, firstId: 9_000);

        return (origen, destino);
    }

    /// <summary>
    /// An appsettings section of the shape the CLI has always read, written to a real
    /// file: the run loads it with <c>LoadConfig</c> and the engine's importer reads the
    /// same text, so anything the file cannot express is not tested here either.
    /// </summary>
    private string EscribirConfig(string origen, string destino, int minimoParaCommit)
    {
        string json = $$"""
        {
          "{{Seccion}}": {
            "Source": {
              "ConnectionString": {{Texto(origen)}},
              "Query": "SELECT Id, Name, Amount, ChangedAt FROM {{Tabla}}"
            },
            "Destination": {
              "ConnectionString": {{Texto(destino)}},
              "StageTable": "dbo.Ledger_stage",
              "FinalTable": "{{Tabla}}"
            },
            "Options": {
              "BatchSize": 5000,
              "MaxDegreeOfParallelism": 1,
              "BulkCopyTimeoutSeconds": 0,
              "KeepIdentity": true,
              "MinRowThresholdToCommit": {{minimoParaCommit}}
            }
          }
        }
        """;

        string ruta = Path.Combine(_carpeta, Seccion + ".json");
        File.WriteAllText(ruta, json, new UTF8Encoding(false));

        return ruta;
    }

    private static string DosSecciones(string origen, string destino) => $$"""
        {
          "Uno": {
            "Source": {
              "ConnectionString": {{Texto(origen)}},
              "Query": "SELECT Id, Name, Amount, ChangedAt FROM dbo.Uno"
            },
            "Destination": {
              "ConnectionString": {{Texto(destino)}},
              "StageTable": "dbo.Uno_stage",
              "FinalTable": "dbo.Uno"
            },
            "Options": {
              "BatchSize": 5000, "MaxDegreeOfParallelism": 1, "BulkCopyTimeoutSeconds": 0,
              "KeepIdentity": true, "MinRowThresholdToCommit": 1
            }
          },
          "Dos": {
            "Source": {
              "ConnectionString": {{Texto(origen)}},
              "Query": "SELECT Id, Name, Amount, ChangedAt FROM dbo.Dos"
            },
            "Destination": {
              "ConnectionString": {{Texto(destino)}},
              "StageTable": "dbo.Dos_stage",
              "FinalTable": "dbo.Dos"
            },
            "Options": {
              "BatchSize": 5000, "MaxDegreeOfParallelism": 1, "BulkCopyTimeoutSeconds": 0,
              "KeepIdentity": true, "MinRowThresholdToCommit": 1
            }
          }
        }
        """;

    private static string Texto(string value) => JsonSerializer.Serialize(value);

    /// <summary>Every row as one string, without the rowversion: the data itself.</summary>
    private static Task<List<string>> FilasAsync(string connectionString, string tabla = Tabla) =>
        PublicationFixture.StringsAsync(connectionString, $"""
            SELECT CONCAT(Id, '|', Name, '|', Amount, '|', ISNULL(CONVERT(nvarchar(30), ChangedAt, 126), ''))
            FROM {tabla}
            ORDER BY Id;
            """);

    /// <summary>
    /// The same, with the rowversion, so that "untouched" is asserted and not inferred:
    /// SQL Server moves a rowversion when and only when the row is actually written.
    /// </summary>
    private static Task<List<string>> FilasConVersionAsync(string connectionString, string tabla = Tabla) =>
        PublicationFixture.StringsAsync(connectionString, $"""
            SELECT CONCAT(Id, '|', Name, '|', Amount, '|', ISNULL(CONVERT(nvarchar(30), ChangedAt, 126), ''),
                          '|', CONVERT(nvarchar(30), CAST(Version AS binary(8)), 1))
            FROM {tabla}
            ORDER BY Id;
            """);
}
