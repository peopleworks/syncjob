using Spectre.Console;
using Spectre.Console.Cli;
using SyncJob.Security;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SyncJob.Commands
{
    // ========================================================================
    // SECRETS - Cifrar las contraseñas del appsettings.json con DPAPI
    // ========================================================================

    public class SecretsSettings : CommandSettings
    {
        [Description("Ruta del appsettings.json")]
        [CommandOption("-c|--config <PATH>")]
        public string ConfigPath { get; set; } = "appsettings.json";

        [Description("Ámbito: 'user' (solo esta cuenta) o 'machine' (cualquier cuenta de esta máquina). Default: user")]
        [CommandOption("--scope <SCOPE>")]
        public string Scope { get; set; } = "user";

        [Description("No crear copia de respaldo del archivo original")]
        [CommandOption("--no-backup")]
        public bool NoBackup { get; set; }

        public override ValidationResult Validate()
        {
            if(!File.Exists(ConfigPath))
                return ValidationResult.Error($"No existe el archivo: {ConfigPath}");

            if(!Scope.Equals("user", StringComparison.OrdinalIgnoreCase) &&
               !Scope.Equals("machine", StringComparison.OrdinalIgnoreCase))
                return ValidationResult.Error("--scope debe ser 'user' o 'machine'");

            return ValidationResult.Success();
        }
    }

    /// <summary>
    /// Reescribe el archivo dejando cifradas las contraseñas de todas las
    /// cadenas de conexión que encuentre. El resto de la cadena (Server,
    /// Database, User Id) queda legible.
    /// </summary>
    public class SecretsProtectCommand : Command<SecretsSettings>
    {
        public override int Execute(CommandContext context, SecretsSettings settings)
        {
            try
            {
                var ambito = settings.Scope.Equals("machine", StringComparison.OrdinalIgnoreCase)
                    ? SecretProtector.Ambito.Maquina
                    : SecretProtector.Ambito.Usuario;

                string json = File.ReadAllText(settings.ConfigPath);
                JsonNode? raiz = JsonNode.Parse(json);
                if(raiz == null)
                {
                    AnsiConsole.MarkupLine("[red]✗[/] El archivo no es JSON válido.");
                    return 1;
                }

                int cifradas = 0, yaEstaban = 0;
                var tocadas = new List<string>();

                RecorrerCadenas(raiz, (ruta, valor) =>
                {
                    if(SecretProtector.EstaCifrado(ExtraerClave(valor)))
                    {
                        yaEstaban++;
                        return valor;
                    }

                    string nuevo = SecretProtector.CifrarClaveDeConexion(valor, ambito);
                    if(nuevo != valor) { cifradas++; tocadas.Add(ruta); }
                    return nuevo;
                });

                if(cifradas == 0)
                {
                    AnsiConsole.MarkupLine(
                        yaEstaban > 0
                            ? $"[green]✓[/] Nada que hacer: las {yaEstaban} contraseñas ya estaban cifradas."
                            : "[yellow]⚠[/] No se encontró ninguna contraseña en las cadenas de conexión.");
                    return 0;
                }

                // El respaldo se hace ANTES de escribir y solo la primera vez.
                // Si se corriera dos veces, un respaldo nuevo pisaria el
                // original en texto plano con uno ya cifrado, y se perderia la
                // unica copia recuperable.
                if(!settings.NoBackup)
                {
                    string respaldo = settings.ConfigPath + ".plain.bak";
                    if(!File.Exists(respaldo))
                    {
                        File.Copy(settings.ConfigPath, respaldo);
                        AnsiConsole.MarkupLine($"[grey]Respaldo:[/] {respaldo}");
                        AnsiConsole.MarkupLine(
                            "[yellow]⚠[/] Ese respaldo tiene las claves EN TEXTO PLANO. " +
                            "Guárdelo aparte y bórrelo del servidor.");
                    }
                }

                File.WriteAllText(
                    settings.ConfigPath,
                    raiz.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

                AnsiConsole.MarkupLine($"[green]✓[/] {cifradas} contraseña(s) cifrada(s) con ámbito [bold]{settings.Scope}[/]");
                foreach(var t in tocadas) AnsiConsole.MarkupLine($"    [grey]{Markup.Escape(t)}[/]");

                if(ambito == SecretProtector.Ambito.Usuario)
                {
                    AnsiConsole.MarkupLine(
                        $"\n[yellow]Ojo:[/] con ámbito [bold]user[/] solo " +
                        $"[bold]{Markup.Escape(Environment.UserDomainName + "\\" + Environment.UserName)}[/] " +
                        $"en [bold]{Environment.MachineName}[/] puede descifrarlas.");
                    AnsiConsole.MarkupLine(
                        "Si un Windows Service va a leer este archivo bajo otra cuenta, use " +
                        "[bold]--scope machine[/] o cifre desde la cuenta del servicio.");
                }

                return 0;
            }
            catch(Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]✗[/] {Markup.Escape(ex.Message)}");
                return 1;
            }
        }

        /// <summary>Devuelve el valor de Password/Pwd dentro de la cadena, o vacío.</summary>
        private static string ExtraerClave(string cadena)
        {
            foreach(var parte in cadena.Split(';'))
            {
                int i = parte.IndexOf('=');
                if(i <= 0) continue;
                string k = parte.Substring(0, i).Trim();
                if(k.Equals("Password", StringComparison.OrdinalIgnoreCase) ||
                   k.Equals("Pwd", StringComparison.OrdinalIgnoreCase))
                    return parte.Substring(i + 1).Trim();
            }
            return string.Empty;
        }

        /// <summary>
        /// Recorre el JSON completo buscando propiedades que parezcan cadenas
        /// de conexión, sin importar cómo esté anidado el archivo. Así funciona
        /// igual con una sección o con veinte, y con estructuras que todavía no
        /// existen.
        /// </summary>
        internal static void RecorrerCadenas(JsonNode nodo, Func<string, string, string> transformar, string ruta = "")
        {
            if(nodo is JsonObject obj)
            {
                foreach(var par in new List<KeyValuePair<string, JsonNode?>>(obj))
                {
                    string hijo = string.IsNullOrEmpty(ruta) ? par.Key : ruta + "." + par.Key;

                    if(par.Value is JsonValue v &&
                       par.Key.IndexOf("ConnectionString", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        string? actual = v.GetValue<string?>();
                        if(!string.IsNullOrWhiteSpace(actual))
                            obj[par.Key] = transformar(hijo, actual!);
                    }
                    else if(par.Value != null)
                    {
                        RecorrerCadenas(par.Value, transformar, hijo);
                    }
                }
            }
            else if(nodo is JsonArray arr)
            {
                for(int i = 0; i < arr.Count; i++)
                    if(arr[i] != null) RecorrerCadenas(arr[i]!, transformar, $"{ruta}[{i}]");
            }
        }
    }

    /// <summary>
    /// Dice qué está cifrado y qué no, sin modificar nada. Es lo primero que
    /// uno quiere correr al llegar a un servidor ajeno.
    /// </summary>
    public class SecretsStatusCommand : Command<SecretsSettings>
    {
        public override int Execute(CommandContext context, SecretsSettings settings)
        {
            try
            {
                JsonNode? raiz = JsonNode.Parse(File.ReadAllText(settings.ConfigPath));
                if(raiz == null) { AnsiConsole.MarkupLine("[red]✗[/] JSON inválido."); return 1; }

                var tabla = new Table().Border(TableBorder.Rounded);
                tabla.AddColumn("Ruta");
                tabla.AddColumn("Servidor / Base");
                tabla.AddColumn("Contraseña");

                int planas = 0;

                SecretsProtectCommand.RecorrerCadenas(raiz, (ruta, valor) =>
                {
                    string servidor = string.Empty, baseDatos = string.Empty, clave = string.Empty;
                    foreach(var parte in valor.Split(';'))
                    {
                        int i = parte.IndexOf('=');
                        if(i <= 0) continue;
                        string k = parte.Substring(0, i).Trim(), val = parte.Substring(i + 1).Trim();
                        if(k.Equals("Server", StringComparison.OrdinalIgnoreCase) ||
                           k.Equals("Data Source", StringComparison.OrdinalIgnoreCase)) servidor = val;
                        else if(k.Equals("Database", StringComparison.OrdinalIgnoreCase) ||
                                k.Equals("Initial Catalog", StringComparison.OrdinalIgnoreCase)) baseDatos = val;
                        else if(k.Equals("Password", StringComparison.OrdinalIgnoreCase) ||
                                k.Equals("Pwd", StringComparison.OrdinalIgnoreCase)) clave = val;
                    }

                    string estado;
                    if(string.IsNullOrEmpty(clave))              estado = "[grey](sin contraseña)[/]";
                    else if(clave.StartsWith("enc:u:"))          estado = "[green]cifrada (usuario)[/]";
                    else if(clave.StartsWith("enc:m:"))          estado = "[green]cifrada (máquina)[/]";
                    else { estado = "[red]TEXTO PLANO[/]"; planas++; }

                    tabla.AddRow(Markup.Escape(ruta), Markup.Escape($"{servidor} / {baseDatos}"), estado);
                    return valor;   // status no modifica
                });

                AnsiConsole.Write(tabla);

                if(planas > 0)
                {
                    AnsiConsole.MarkupLine(
                        $"\n[red]{planas}[/] contraseña(s) en texto plano. Ciffrelas con:");
                    AnsiConsole.MarkupLine($"  [grey]SyncJob.exe secrets protect -c {Markup.Escape(settings.ConfigPath)}[/]");
                    return 2;
                }

                AnsiConsole.MarkupLine("\n[green]✓[/] Ninguna contraseña en texto plano.");
                return 0;
            }
            catch(Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]✗[/] {Markup.Escape(ex.Message)}");
                return 1;
            }
        }
    }
}
