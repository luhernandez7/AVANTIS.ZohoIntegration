using AVS_Common_Encriptacion;
using AVS_ZohoIntegration.Manager;
using AVSSAPConector.DTO;
using log4net;
using System;
using System.Collections.Generic;
using System.IO;

namespace AVS_ZohoIntegration
{
    class Program
    {
        private static ILog log = null;

        static void Main(string[] args)
        {
            try
            {
                var success = ValidateArgument(args);
                if (!success)
                    return;

                string argumento = args[0].Trim();
                ChangeLogFileName(argumento, "AVS_ZohoIntegration.log");

                log.Info($"AVS ZohoIntegration Version [{System.Reflection.Assembly.GetExecutingAssembly().GetName().Version}]");
                log.Info("Leyendo archivo de configuracion.");

                var companiesConfig = Newtonsoft.Json.JsonConvert.DeserializeObject<List<Company>>(System.Configuration.ConfigurationManager.AppSettings["companies:Config"]);
                log.Info("Archivo de configuracion leido correctamente.");
                foreach (var Company in companiesConfig)
                {
                    try
                    {
                        log.Debug($"**************************************************************************");
                        log.Debug($"Ejecutando sincronizacion para: {Company.dbName}");
                        Company.dbUserPassword = Encriptacion.Desencriptar(Company.dbUserPassword);
                        Company.sapUserPassword = Encriptacion.Desencriptar(Company.sapUserPassword);

                        ZohoIntegrationManager manager = new ZohoIntegrationManager(log, Company);
                        manager.IniciarProcesamientoDocumento(argumento);
                    }
                    catch (Exception Ex)
                    {
                        log.Error($"Se ha detectado un error, durante la sincronizacion. Error: {Ex.Message}");
                    }
                    finally
                    {
                        log.Info($"Cerrando conexión con SAP");
                        Company.Disconnect();
                        log.Info("Conexión SAP cerrada correctamente.");
                        log.Info($"**************************************************************************");
                    }
                }
            }
            catch (Exception ex)
            {
                log.Error(ex.Message);
            }
        }

        private static void ChangeLogFileName(string arg, string namefile)
        {
            var logPath = Path.Combine("Logs", arg);
            Directory.CreateDirectory(logPath);
            log4net.GlobalContext.Properties["LogFileName"] = Path.Combine(logPath, namefile);
            log4net.Config.XmlConfigurator.Configure();
            log = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);
        }

        private static bool ValidateArgument(string[] args)
        {
            if (args.Length != 1 || string.IsNullOrEmpty(args[0]))
            {
                ChangeLogFileName("ErrorArgumento", "AVS_ZohoIntegration.log");

                string mensajeError;
                if (args.Length == 0)
                    mensajeError = "Proceso detenido: No se recibió ningún argumento. Se requiere exactamente uno para iniciar la ejecución.";
                else if (args.Length > 1)
                    mensajeError = "Proceso detenido: Se detectaron múltiples argumentos. La aplicación solo acepta uno por ejecución.";
                else if (string.IsNullOrEmpty(args[0]))
                    mensajeError = "Proceso detenido: El argumento proporcionado está vacío o no es válido.";
                else
                    mensajeError = "Proceso detenido: Error de validación en los argumentos.";

                log.Error(mensajeError);
                return false;
            }

            return true;
        }
    }
}
