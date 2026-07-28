using AVS_LicValidator;
using AVSSAPConector.DTO;
using log4net;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace AVS_ZohoIntegration.Manager
{
    public class ZohoIntegrationManager
    {
        #region Variables
        private static ILog log;
        private Company company;

        private static readonly HttpClient _httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        private static readonly HttpClient _authClient = new HttpClient();

        private static string _currentAccessToken = null;
        private static DateTime _tokenExpiration = DateTime.MinValue;
        #endregion

        public ZohoIntegrationManager(ILog Log, Company oCompany)
        {
            log = Log;
            company = oCompany;
            this.company.OpenSAPConn();

            #region Validador de licencias 
            var RFC = company.Get_CompanyLicTradNum();
            LicManager lm = new LicManager();
            log.Debug("Validando licencia...");
            var licFilePath = ConfigurationManager.AppSettings["licFilePath"];
            //lm.LicenseValidator("AVS_ZohoIntegration", RFC, licFilePath);
            log.Info("Licencia valida.");
            #endregion
        }

        public void IniciarProcesamientoDocumento(string comando)
        {
            log.Info($"Comando recibido: {comando}");

            try
            {
                switch (comando.ToUpperInvariant())
                {
                    //case "SENDACCOUNTS":
                    //    log.Info("Iniciando proceso de envío de información de cuentas de mayor...");
                    //    SendEntityToZohoAsync("OACT", "Cuentas de Mayor").GetAwaiter().GetResult();
                    //    break;

                    case "SENDITEMS":
                        log.Info("Iniciando proceso de envío de artículos...");
                        SendEntityToZohoAsync("OITM", "Artículos").GetAwaiter().GetResult();
                        break;

                    case "SENDBUSINESSPARTNERS":
                        log.Info("Iniciando proceso de envío de socios de negocio...");
                        SendEntityToZohoAsync("OCRD", "Socios de Negocio").GetAwaiter().GetResult();
                        break;

                    case "SENDCONTACTPERSONS":
                        log.Info("Iniciando proceso de envío de socios de negocio...");
                        SendEntityToZohoAsync("OCPR", "Personas de contacto").GetAwaiter().GetResult();
                        break;

                    default:
                        log.Error($"El comando '{comando}' no está configurado en el desarrollo.");
                        break;
                }
            }
            catch (Exception ex)
            {
                log.Error($"Ocurrió un error general procesando el comando {comando}:", ex);
            }
            finally
            {
                log.Info("Proceso finalizado.");
            }
        }

        public async Task<string> GetValidAccessTokenAsync()
        {
            // Si el token actual todavía es válido (con un margen de 5 minutos de seguridad), lo reutilizamos
            if (!string.IsNullOrEmpty(_currentAccessToken) && DateTime.Now < _tokenExpiration.AddMinutes(-5))
            {
                return _currentAccessToken;
            }

            // Si expiró o es la primera vez, solicitamos uno nuevo
            log.Info("El Access Token de Zoho ha expirado o no existe. Solicitando uno nuevo...");

            string clientId = ConfigurationManager.AppSettings["ZOHO:ClientId"];
            string clientSecret = ConfigurationManager.AppSettings["ZOHO:ClientSecret"];
            string refreshToken = ConfigurationManager.AppSettings["ZOHO:RefreshToken"];
            string accountsUrl = ConfigurationManager.AppSettings["ZOHO:AccountsUrl"];

            var requestBody = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("refresh_token", refreshToken),
                new KeyValuePair<string, string>("client_id", clientId),
                new KeyValuePair<string, string>("client_secret", clientSecret),
                new KeyValuePair<string, string>("grant_type", "refresh_token")
            });

            try
            {
                HttpResponseMessage response = await _authClient.PostAsync(accountsUrl, requestBody);
                response.EnsureSuccessStatusCode();

                string responseJson = await response.Content.ReadAsStringAsync();
                JObject tokenData = JObject.Parse(responseJson);

                if (tokenData["access_token"] != null)
                {
                    _currentAccessToken = tokenData["access_token"].ToString();

                    // Zoho normalmente devuelve expires_in = 3600 (1 hora en segundos)
                    int expiresInSeconds = tokenData["expires_in"] != null ? (int)tokenData["expires_in"] : 3600;
                    _tokenExpiration = DateTime.Now.AddSeconds(expiresInSeconds);

                    log.Info("Nuevo Access Token de Zoho obtenido correctamente.");
                    return _currentAccessToken;
                }
                else
                {
                    throw new Exception("La respuesta de Zoho no incluyó un Access Token.");
                }
            }
            catch (Exception ex)
            {
                log.Error("Error crítico al intentar renovar el token de Zoho:", ex);
                throw;
            }
        }

        private async Task SendEntityToZohoAsync(string tableName, string entityDescription)
        {
            string query = ConfigurationManager.AppSettings[$"SAP:ZOHO:{tableName}:Query"];
            string[] fields = ConfigurationManager.AppSettings[$"SAP:ZOHO:{tableName}:Fields"].Split(',');
            string apiEndpoint = ConfigurationManager.AppSettings[$"SAP:ZOHO:{tableName}:API"];

            string detailNodesConfig = ConfigurationManager.AppSettings[$"SAP:ZOHO:{tableName}:DetailNodes"];
            string[] detailNodes = !string.IsNullOrEmpty(detailNodesConfig) ? detailNodesConfig.Split(',') : new string[0];

            log.Info($"*** Obteniendo información de cabecera: {entityDescription} ({tableName}) ***");

            var records = GetValuesByDocuments(tableName, query, fields);
            if (records == null || !records.Any())
            {
                log.Info($"** No hay registros pendientes de {tableName} para procesar.");
                return;
            }

            foreach (var nodeName in detailNodes)
            {
                string cleanNodeName = nodeName.Trim();
                log.Info($"* Consultando sub-entidad [{cleanNodeName}] para {tableName}...");

                string nodePrefix = $"SAP:ZOHO:{tableName}:{cleanNodeName}";
                string detailQueryTemplate = ConfigurationManager.AppSettings[$"{nodePrefix}:Query"];
                string[] detailFields = ConfigurationManager.AppSettings[$"{nodePrefix}:Fields"].Split(',');
                string detailKey = ConfigurationManager.AppSettings[$"{nodePrefix}:DetailKey"];
                string arrayName = ConfigurationManager.AppSettings[$"{nodePrefix}:DetailArrayName"];
                string isSingleObjectConfig = ConfigurationManager.AppSettings[$"{nodePrefix}:IsSingleObject"];
                bool isSingleObject = !string.IsNullOrEmpty(isSingleObjectConfig) && isSingleObjectConfig.Equals("true", StringComparison.OrdinalIgnoreCase);

                // ---------------------------------------------------------
                // LA MAGIA DEL FILTRO MASIVO (Evitando el problema N+1)
                // ---------------------------------------------------------

                var keysToSearch = records
                    .Where(r => r.ContainsKey(detailKey) && r[detailKey] != null)
                    .Select(r => $"'{r[detailKey]}'")
                    .Distinct()
                    .ToList();

                if (!keysToSearch.Any()) 
                    continue; // Si no hay llaves, saltamos al siguiente detalle

                string inClauseValues = string.Join(",", keysToSearch);
                string finalDetailQuery = detailQueryTemplate.Replace("@InClause", inClauseValues);
                var detailRecords = GetValuesByDocuments($"{tableName}_{cleanNodeName}", finalDetailQuery, detailFields);

                // ---------------------------------------------------------

                if (detailRecords != null && detailRecords.Any())
                {
                    foreach (var header in records)
                    {
                        string keyValue = header[detailKey]?.ToString();
                        var matchingDetails = detailRecords
                            .Where(d => d.ContainsKey(detailKey) && d[detailKey]?.ToString() == keyValue)
                            .Select(d =>
                            {
                                var cleanDetail = new Dictionary<string, object>(d);
                                cleanDetail.Remove(detailKey);
                                return cleanDetail;
                            })
                            .ToList();

                        if (isSingleObject)
                            header.Add(arrayName, matchingDetails.FirstOrDefault());
                        else
                            header.Add(arrayName, matchingDetails);
                    }
                }
            }

            var zohoPayload = new { data = records };
            string jsonPayload = JsonConvert.SerializeObject(zohoPayload);
            log.Info($"** {records.Count} registros listos. Enviando a Zoho...");

            try
            {
                JObject response = await PostTransactionAsync(apiEndpoint, jsonPayload);

                if (HasErrors(response))
                {
                    log.Warn($"**** Error al procesar información de {tableName} ****");
                    UpdateTableWithError(response, tableName);
                }
                else
                {
                    log.Info($"**** Entidad {tableName} procesada con éxito ****");
                    UpdateTableWithoutError(tableName);
                }
            }
            catch (Exception ex)
            {
                log.Error($"**** Excepción crítica al enviar {tableName} a Zoho ****", ex);
            }
        }

        private List<Dictionary<string, object>> GetValuesByDocuments(string tableName, string query, string[] fields)
        {
            // 1. Validar configuración antes de consultar la base de datos
            if (fields == null || fields.Length == 0)
            {
                throw new ArgumentException($"No se han configurado campos en [\"SAP:ZOHO:{tableName}:Fields\"].");
            }

            var itemsGroups = new List<Dictionary<string, object>>();
            var recordSet = company.ExecuteQuery(query, log);

            // 2. Validar si hay registros (Cláusula de guarda)
            if (recordSet == null || recordSet.Count == 0)
            {
                log.Info($"No se encontraron resultados pendientes de procesar ({tableName}).");
                return itemsGroups;
            }

            log.Info($"Se han obtenido {recordSet.Count} resultados pendientes de procesar.");

            int index = 1;
            foreach (var item in recordSet)
            {
                try
                {
                    log.Info($"* Procesando resultado: {index} *");
                    var itemGroup = new Dictionary<string, object>();

                    foreach (var field in fields)
                    {
                        string cleanField = field.Trim();

                        // Asegurar que el valor no sea nulo antes de invocar ToString()
                        object value = item[cleanField];
                        string stringValue = value?.ToString() ?? string.Empty;

                        itemGroup.Add(cleanField, stringValue);
                        log.Info($"{cleanField}: {stringValue}");
                    }
                    itemsGroups.Add(itemGroup);
                }
                catch (Exception ex)
                {
                    // Nunca dejar un catch vacío. Registrar el error del registro específico.
                    log.Error($"Error al construir el diccionario para el registro {index} de {tableName}: {ex.Message}");
                }
                index++;
            }

            return itemsGroups;
        }

        private bool HasErrors(JObject response)
        {
            // Aquí implementas la lógica según cómo responda la API de Zoho.
            // Ejemplo: return response["code"]?.ToString() != "0";
            return false;
        }

        private void UpdateTableWithError(JObject JsonBeluga, string Tabla)
        {
            try
            {
                if (!string.IsNullOrEmpty(Tabla))
                    return;

                if (JsonBeluga.Count > 0)
                {
                    log.Info("Leyendo información del Json de Beluga");
                    foreach (JToken child in JsonBeluga.Children())
                    {
                        if (child is JProperty property)
                        {
                            if (Tabla != property.Name)
                                continue;

                            log.Info($"Tabla por procesar: {property.Name}");

                            try
                            {
                                if (property.Value is JObject nestedObject)
                                {
                                    if (nestedObject["ERR"].First != null)
                                    {
                                        foreach (var childERR in nestedObject["ERR"].First.Children())
                                        {
                                            var itemCode = new Dictionary<string, object>();
                                            switch (property.Name)
                                            {
                                                case "OCRG":
                                                    itemCode.Add("GroupCode", childERR["Code"].ToString());
                                                    break;
                                                case "OCRN":
                                                    itemCode.Add("CurrCode", childERR["Code"].ToString());
                                                    break;
                                                case "OSLP":
                                                    itemCode.Add("SlpCode", childERR["Code"].ToString());
                                                    break;
                                                case "OCTG":
                                                    itemCode.Add("GroupNum", childERR["Code"].ToString());
                                                    break;
                                                case "OPYM":
                                                    itemCode.Add("PayMethCod", childERR["Code"].ToString());
                                                    break;
                                                case "OACT":
                                                    itemCode.Add("AcctCode", childERR["Code"].ToString());
                                                    break;
                                                case "OSTA":
                                                    itemCode.Add("Code", childERR["Code"].ToString());
                                                    itemCode.Add("Type", childERR["Type"].ToString());
                                                    break;
                                                case "OWHT":
                                                    itemCode.Add("WTCode", childERR["Code"].ToString());
                                                    break;
                                                default:
                                                    throw new Exception($"Tabla {property.Name} no incluida para sincronización. Tablas permitidas [OCRG,OCRN,OSLP,OCTG,OPYM,OACT,OSTA,OWHT]");
                                            }

                                            UpdateUserFieldsSAPToBLS(property.Name, itemCode, "-1", childERR["Message"].ToString());
                                        }
                                    }
                                    else
                                        log.Info($"No se han encontrado registros con error. Registros con exito {nestedObject["SUCCESS"]}");
                                }
                                else
                                    throw new Exception($"La tabla {property.Name} dentro del Json no cumple con las caracteristicas necesarias. - {property.Value}");
                            }
                            catch (Exception Ex)
                            {
                                log.Error(Ex.Message);
                            }
                        }
                        else
                            throw new Exception($"El Json devuelto por BL System no cuenta con las características necesarias. - {JsonBeluga}");
                    }
                }
                else
                    throw new Exception($"El Json devuelto por BL System no cuenta con las características necesarias. - {JsonBeluga}");
            }
            catch (Exception Ex)
            {
                log.Error(Ex.Message);
            }
        }

        private void UpdateTableWithoutError(string Table)
        {
            try
            {
                var Query_Pendingtables = "SELECT * FROM @Table WHERE \"U_AVS_BLS_Sync\" = 2";
                log.Info($"Procesando información de la tabla {Table}");
                var queryPendingtables = Query_Pendingtables.Replace("@Table", Table);
                var keysPendingtables = string.Empty;

                switch (Table)
                {
                    case "OCRG":
                        keysPendingtables = "GroupCode";
                        break;
                    case "OCRN":
                        keysPendingtables = "CurrCode";
                        break;
                    case "OSLP":
                        keysPendingtables = "SlpCode";
                        break;
                    case "OCTG":
                        keysPendingtables = "GroupNum";
                        break;
                    case "OPYM":
                        keysPendingtables = "PayMethCod";
                        break;
                    case "OACT":
                        keysPendingtables = "AcctCode";
                        break;
                    case "OSTA":
                        keysPendingtables = "Code,Type";
                        break;
                    case "OWHT":
                        keysPendingtables = "WTCode";
                        break;
                    default:
                        throw new Exception($"Tabla {Table} no incluida para sincronización. Tablas permitidas [OCRG,OCRN,OSLP,OCTG,OPYM,OACT,OSTA,OWHT]");
                }

                var RecorsetPendingtables = company.ExecuteQuery(queryPendingtables, keysPendingtables, log);
                if (RecorsetPendingtables.Count > 0)
                {
                    log.Info($"Se han encontrado {RecorsetPendingtables.Count} registros pendientes por actualizar");
                    foreach (var item in RecorsetPendingtables)
                    {
                        try
                        {
                            UpdateUserFieldsSAPToBLS(Table, item, "3");
                        }
                        catch (Exception Ex)
                        {
                            log.Error(Ex.Message);
                        }
                    }
                }
                else
                    log.Info($"No se encontraron registros pendientes por actualizar en la tabla {Table}");

            }
            catch (Exception Ex)
            {
                log.Error($"No se pudieron actualizar los registros de la tabla {Table}. {Ex.Message}");
            }

        }

        private void UpdateUserFieldsSAPToBLS(string Table, Dictionary<string, object> item, string status, string message = "")
        {
            Dictionary<string, object> userFields = new Dictionary<string, object>();
            userFields.Add("U_AVS_BLS_Sync", status);
            if (status == "-1")
            {
                userFields.Add("U_AVS_BLS_ProcResult", message);
                log.Error(message);
            }
            else
                userFields.Add("U_AVS_BLS_ProcResult", "");

            switch (Table)
            {
                case "OACT":
                    log.Info("-- Actualizando cuentas de mayor --");
                    company.UPDATE_OACT(Convert.ToString(item["AcctCode"]), userFields);
                    log.Info("- Cuentas de mayor actualizado con exito--");
                    break;
                default:
                    throw new Exception($"Tabla {Table} no incluida para sincronización. Tablas permitidas [OCRG,OCRN,OSLP,OCTG,OPYM,OACT,OSTA,OWHT,OPRC]");
            }
        }

        private async Task<JObject> PostTransactionAsync(string requestUri, string jsonPayload)
        {
            try
            {
                // 1. Obtener el token (Si ya es válido, te lo da instantáneo; si no, va a Zoho por uno nuevo)
                string accessToken = await GetValidAccessTokenAsync();

                // 2. Inyectar el token en el HttpClient
                _httpClient.DefaultRequestHeaders.Clear();
                _httpClient.DefaultRequestHeaders.Add("Authorization", $"Zoho-oauthtoken {accessToken}");

                log.Info($"Enviando solicitud POST a {requestUri}...");

                using (var content = new StringContent(jsonPayload, System.Text.Encoding.UTF8, "application/json"))
                {
                    using (HttpResponseMessage response = await _httpClient.PostAsync(requestUri, content))
                    {
                        // Si Zoho nos rechaza por permisos o datos incorrectos, capturamos el JSON de error
                        string responseString = await response.Content.ReadAsStringAsync();
                        if (!response.IsSuccessStatusCode)
                        {
                            log.Warn($"Respuesta fallida de Zoho ({response.StatusCode}): {responseString}");
                        }

                        return JObject.Parse(responseString);
                    }
                }
            }
            catch (Exception ex)
            {
                log.Error($"Error de red al consumir API POST {requestUri}: {ex.Message}");
                throw;
            }
        }

    }

}
