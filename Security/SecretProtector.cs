using System;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace SyncJob.Security
{
    /// <summary>
    /// Cifrado de secretos con DPAPI de Windows.
    ///
    /// La herramienta ya cifraba las credenciales guardadas en SQLite, pero la
    /// ruta del JSON las dejaba en texto plano — y ese archivo termina viviendo
    /// en el servidor del cliente, junto al ejecutable. Aqui se reusa el mismo
    /// mecanismo que ya estaba construido.
    ///
    /// FORMATO. Un valor cifrado se reconoce por su prefijo:
    ///
    ///     enc:u:BASE64...   cifrado para el USUARIO actual
    ///     enc:m:BASE64...   cifrado para la MAQUINA
    ///
    /// El ambito va DENTRO del valor a proposito: al descifrar no hay que
    /// adivinar con cual se cifro, ni guardarlo en otro lado que se pueda
    /// desincronizar.
    ///
    /// Un valor sin prefijo se devuelve tal cual. Asi un appsettings.json viejo
    /// en texto plano sigue funcionando sin tocarlo: el cifrado se adopta
    /// cuando se quiere, no de golpe.
    ///
    /// QUE PROTEGE Y QUE NO. DPAPI ata el secreto a Windows: la llave la
    /// administra el sistema operativo, no esta en el archivo ni en el binario.
    /// Copiar el JSON a otra maquina no sirve de nada. Lo que NO protege es de
    /// alguien que ya ejecuta codigo como el mismo usuario en la misma maquina
    /// — para ese, el secreto es legible. Es el limite del mecanismo, no un
    /// descuido.
    /// </summary>
    public static class SecretProtector
    {
        private const string Prefijo        = "enc:";
        private const string MarcaUsuario   = "enc:u:";
        private const string MarcaMaquina   = "enc:m:";

        /// <summary>
        /// CurrentUser  - solo el usuario que cifro puede descifrar. Mas fuerte.
        /// LocalMachine - cualquier cuenta de esa maquina puede descifrar.
        ///
        /// La eleccion NO es cosmetica: si el JSON se cifra con una sesion
        /// interactiva y el Windows Service corre bajo otra cuenta, con
        /// CurrentUser el servicio NO va a poder leerlo. Para servicios se usa
        /// LocalMachine, o se cifra desde la misma cuenta del servicio.
        /// </summary>
        public enum Ambito
        {
            Usuario,
            Maquina
        }

        public static bool EstaCifrado(string? valor)
        {
            return !string.IsNullOrEmpty(valor)
                && valor!.StartsWith(Prefijo, StringComparison.Ordinal);
        }

        public static string Cifrar(string textoPlano, Ambito ambito = Ambito.Usuario)
        {
            if (string.IsNullOrEmpty(textoPlano)) return textoPlano;
            if (EstaCifrado(textoPlano)) return textoPlano;   // ya lo estaba
            VerificarWindows();

            byte[] datos = ProtectedData.Protect(
                Encoding.UTF8.GetBytes(textoPlano),
                null,
                ambito == Ambito.Maquina
                    ? DataProtectionScope.LocalMachine
                    : DataProtectionScope.CurrentUser);

            string marca = ambito == Ambito.Maquina ? MarcaMaquina : MarcaUsuario;
            return marca + Convert.ToBase64String(datos);
        }

        /// <summary>
        /// Descifra si viene marcado; si no, devuelve el valor sin tocar.
        /// </summary>
        public static string Descifrar(string? valor)
        {
            if (string.IsNullOrEmpty(valor)) return valor ?? string.Empty;
            if (!EstaCifrado(valor)) return valor!;
            VerificarWindows();

            DataProtectionScope ambito;
            string base64;

            if (valor!.StartsWith(MarcaUsuario, StringComparison.Ordinal))
            {
                ambito = DataProtectionScope.CurrentUser;
                base64 = valor.Substring(MarcaUsuario.Length);
            }
            else if (valor.StartsWith(MarcaMaquina, StringComparison.Ordinal))
            {
                ambito = DataProtectionScope.LocalMachine;
                base64 = valor.Substring(MarcaMaquina.Length);
            }
            else
            {
                throw new FormatException(
                    "Valor cifrado con formato desconocido. Se esperaba 'enc:u:' o 'enc:m:'.");
            }

            try
            {
                return Encoding.UTF8.GetString(
                    ProtectedData.Unprotect(Convert.FromBase64String(base64), null, ambito));
            }
            catch (CryptographicException ex)
            {
                // El error crudo de DPAPI ("Key not valid for use in specified
                // state") no le dice nada a nadie. Este mensaje sale justo
                // cuando alguien copio el archivo cifrado a otra maquina, o
                // cuando el servicio corre bajo una cuenta distinta a la que
                // cifro — que es el error que mas se va a cometer.
                string pista = ambito == DataProtectionScope.CurrentUser
                    ? $"Se cifró para otro usuario o en otra máquina. Ahora corre como " +
                      $"'{Environment.UserDomainName}\\{Environment.UserName}' en '{Environment.MachineName}'. " +
                      "Vuelva a cifrar desde esta cuenta, o use --scope machine si un servicio " +
                      "tiene que leerlo."
                    : $"Se cifró en otra máquina. Ahora corre en '{Environment.MachineName}'. " +
                      "Vuelva a cifrar en esta máquina.";

                throw new InvalidOperationException("No se pudo descifrar el secreto. " + pista, ex);
            }
        }

        /// <summary>
        /// Cifra SOLO la contraseña dentro de una cadena de conexión.
        ///
        /// Se deja legible el resto (Server, Database, User Id) a proposito: en
        /// una emergencia hay que poder ver a que servidor apunta un job sin
        /// descifrar nada, y un diff del archivo tiene que seguir siendo util.
        /// Lo secreto es la clave, no la topologia.
        /// </summary>
        public static string CifrarClaveDeConexion(string cadena, Ambito ambito = Ambito.Usuario)
        {
            return TransformarClave(cadena, valor => Cifrar(valor, ambito));
        }

        public static string DescifrarClaveDeConexion(string cadena)
        {
            return TransformarClave(cadena, Descifrar);
        }

        /// <summary>
        /// Recorre la cadena por segmentos y aplica la transformacion al valor
        /// de Password / Pwd. Se hace a mano y no con SqlConnectionStringBuilder
        /// porque el builder normaliza y reordena toda la cadena, y un archivo
        /// de configuracion tiene que quedar como el usuario lo escribio.
        /// </summary>
        private static string TransformarClave(string cadena, Func<string, string> transformar)
        {
            if (string.IsNullOrWhiteSpace(cadena)) return cadena;

            var partes = cadena.Split(';');
            for (int i = 0; i < partes.Length; i++)
            {
                int igual = partes[i].IndexOf('=');
                if (igual <= 0) continue;

                string clave = partes[i].Substring(0, igual).Trim();
                if (!clave.Equals("Password", StringComparison.OrdinalIgnoreCase) &&
                    !clave.Equals("Pwd", StringComparison.OrdinalIgnoreCase))
                    continue;

                string valor = partes[i].Substring(igual + 1);
                // Se conserva el espaciado original alrededor del '=' para que
                // el archivo no cambie mas de lo necesario.
                string sangria = partes[i].Substring(0, igual).Replace(clave, string.Empty);
                partes[i] = sangria + clave + "=" + transformar(valor.Trim());
            }

            return string.Join(";", partes);
        }

        private static void VerificarWindows()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                throw new PlatformNotSupportedException(
                    "El cifrado de secretos usa DPAPI y solo está disponible en Windows.");
        }
    }
}
