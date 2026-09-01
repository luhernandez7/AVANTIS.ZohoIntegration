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

        #region Common
        public void IniciarProcesamientoDocumento(string comando)
        {
            log.Info($"Comando recibido: {comando}");

            try
            {
                switch (comando.ToUpperInvariant())
                {

                    case "DBCHECK":
                        log.Debug("Iniciando preparación de estructuras de base de datos...");
                        DbCheck();
                        break;

                    case "SENDITEMS":
                        log.Info("Iniciando proceso de envío de artículos...");
                        SendEntityToZohoAsync("OITM", "Artículos").GetAwaiter().GetResult();
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

        private void DbCheck()
        {
            List<UDTDef> tablas = new List<UDTDef>
            {
                new UDTDef
                {
                     TableName = "@AVS_ZOHO_LOG",
                    Description = "Zoho Sync Log",
                    TableType = (BoUTBTableType)0
                }
            };

            List<UDFDef> campos = new List<UDFDef>
            {
                new UDFDef
                {
                    TableName = "OITM",
                    FieldName = "AVS_Zoho_Sync",
                    Description = "Estatus Sincronización Zoho",
                    Size = 2,
                    ValidValues = new List<(string Value, string Description)> { ("0", "Pendiente"), ("1", "Error"), ("2", "No sincronizar"), ("3", "Sincronizado") },
                    DefaultValue = "0"
                },
                new UDFDef
                {
                    TableName = "OITM",
                    FieldName = "AVS_Zoho_ProcResult",
                    Description = "Resultado o Error de Sincronización",
                    Size = 254
                },
                new UDFDef
                {
                    TableName = "@AVS_ZOHO_LOG",
                    FieldName = "LastSync",
                    Description = "Ultima Fecha Sincronizacion",
                    FieldType = BoFieldTypes.db_Date
                },
                new UDFDef
                {
                    TableName = "@AVS_ZOHO_LOG",
                    FieldName = "LastTime",
                    Description = "Ultima Hora Sincronizacion",
                    FieldType = BoFieldTypes.db_Date,
                    SubType = BoFldSubTypes.st_Time
                }
            };

            log.Debug("Enviando listas a CrearEstructuras...");
            company.CrearEstructuras(tablas, campos, null, log);
        }

        public async Task<string> GetValidAccessTokenAsync()
        {
            if (!string.IsNullOrEmpty(_currentAccessToken) && DateTime.Now < _tokenExpiration.AddMinutes(-5))
            {
                return _currentAccessToken;
            }

            log.Info("El Access Token de Zoho ha expirado o no existe. Solicitando uno nuevo...");

            string accountsUrl = company.Zoho_AccountsUrl;
            string clientId = company.Zoho_ClientId;
            string clientSecret = company.Zoho_ClientSecret;
            string refreshToken = company.Zoho_RefreshToken;

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
                    int expiresInSeconds = tokenData["expires_in"] != null ? (int)tokenData["expires_in"] : 3600;
                    _tokenExpiration = DateTime.Now.AddSeconds(expiresInSeconds);

                    log.Info("Nuevo Access Token de Zoho obtenido correctamente.");
                    return _currentAccessToken;
                }
                else
                    throw new Exception("La respuesta de Zoho no incluyó un Access Token.");
            }
            catch (Exception ex)
            {
                log.Error("Error crítico al intentar renovar el token de Zoho:", ex);
                throw;
            }
        }

        #endregion

        #region Send
        private async Task SendEntityToZohoAsync(string configKey, string entityDescription)
        {
            if (!company.Zoho_EntityConfig.TryGetValue(configKey, out ZohoEntityConfig entityConfig))
            {
                log.Error($"La configuración para '{configKey}' no existe en el JSON.");
                return;
            }

            log.Info("Obteniendo ultima fecha de sincronización");
            string lastSyncDate = ObtenerUltimaFechaSincronizacionDesdeSAP(configKey);
            if (string.IsNullOrEmpty(lastSyncDate))
            {
                lastSyncDate = DateTime.Now.AddDays(-1).ToString("yyyy-MM-dd HH:mm:ss");
                log.Warn($"No se encontró fecha de última sincronización para {configKey}. Aplicando fallback de seguridad: {lastSyncDate}");
            }
            else
                log.Info($"Última sincronización exitosa registrada para {configKey}: {lastSyncDate}");

            string query = entityConfig.Query.Replace("@LastSyncDate", $"'{lastSyncDate}'");
            string[] fields = entityConfig.Fields.Split(',');
            string apiEndpoint = entityConfig.API;

            log.Info($"*** Obteniendo información de cabecera: {entityDescription} ({configKey}) ***");

            var records = GetValuesByDocuments(configKey, query, fields);
            if (records == null || !records.Any())
            {
                log.Info($"** No hay registros pendientes de {configKey} para procesar.");
                return;
            }

            if (entityConfig.DetailNodes != null && entityConfig.DetailNodes.Any())
            {
                foreach (var node in entityConfig.DetailNodes)
                {
                    string cleanNodeName = node.Key.Trim();
                    ZohoDetailNodeConfig detailConfig = node.Value;
                    log.Info($"* Consultando sub-entidad [{cleanNodeName}] para {configKey}...");

                    string detailQueryTemplate = detailConfig.Query;
                    string[] detailFields = detailConfig.Fields.Split(',');
                    string detailKey = detailConfig.DetailKey;
                    string arrayName = detailConfig.DetailArrayName;
                    bool isSingleObject = detailConfig.IsSingleObject;

                    var keysToSearch = records
                        .Where(r => r.ContainsKey(detailKey) && r[detailKey] != null)
                        .Select(r => $"'{r[detailKey]}'")
                        .Distinct()
                        .ToList();

                    if (!keysToSearch.Any())
                        continue;

                    string inClauseValues = string.Join(",", keysToSearch);
                    string finalDetailQuery = detailQueryTemplate.Replace("@InClause", inClauseValues);
                    var detailRecords = GetValuesByDocuments($"{configKey}_{cleanNodeName}", finalDetailQuery, detailFields);

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
                                }).ToList();

                            if (isSingleObject)
                                header.Add(arrayName, matchingDetails.FirstOrDefault());
                            else
                                header.Add(arrayName, matchingDetails);
                        }
                    }
                }
            }

            var normalizedRecords = records.Select(record =>
                record.ToDictionary(
                    kvp => kvp.Key,
                    kvp =>
                    {
                        var val = kvp.Value;
                        if (val == null)
                            return null;

                        string strVal = val.ToString().Trim();

                        if (strVal.Equals("true", StringComparison.OrdinalIgnoreCase) || strVal.Equals("false", StringComparison.OrdinalIgnoreCase))
                            return bool.Parse(strVal);

                        if (strVal.Equals("Y", StringComparison.OrdinalIgnoreCase) || strVal.Equals("N", StringComparison.OrdinalIgnoreCase))
                            return strVal.Equals("Y", StringComparison.OrdinalIgnoreCase);

                        if (decimal.TryParse(strVal, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out decimal decVal))
                            return decVal;

                        return val;
                    }
                )
            ).ToList();

            var apiPayload = new { data = normalizedRecords };
            string jsonPayload = JsonConvert.SerializeObject(apiPayload);
            log.Info($"** {records.Count} registros listos para Upsert. Enviando a Zoho...");

            try
            {
                string upsertEndpoint = apiEndpoint.EndsWith("/upsert", StringComparison.OrdinalIgnoreCase) ? apiEndpoint : apiEndpoint.TrimEnd('/') + "/upsert";
                JObject response = await PostTransactionAsync(upsertEndpoint, jsonPayload);
                ProcesarRespuestaZoho_Send(response, configKey, records, entityConfig.SapKeyField);

                string fechaHoraActual = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                log.Info("Actualizando tabla de sincronización.");
                company.ActualizarUltimaFechaSincronizacionEnSAP(configKey, fechaHoraActual, log);
                log.Info("Tabla de sincronización actualizada con exito.");
            }
            catch (Exception ex)
            {
                log.Error($"**** Excepción crítica al enviar {configKey} a Zoho ****", ex);
            }
        }

        private string ObtenerUltimaFechaSincronizacionDesdeSAP(string configKey)
        {
            try
            {
                string query = $@"SELECT ""U_LastSync"", ""U_LastTime"" FROM ""@AVS_ZOHO_LOG"" WHERE ""Code"" = '{configKey}'";
                var recordSet = company.ExecuteQuery(query);
                if (recordSet != null && recordSet.Count > 0)
                {
                    var fechaStr = recordSet[0]["U_LastSync"]?.ToString();
                    var horaStr = recordSet[0]["U_LastTime"]?.ToString();

                    if (!string.IsNullOrEmpty(fechaStr))
                    {
                        if (string.IsNullOrEmpty(horaStr))
                            horaStr = "00:00:00";

                        DateTime parsedDate = Convert.ToDateTime(fechaStr);
                        string fechaLimpia = parsedDate.ToString("yyyy-MM-dd");
                        return $"{fechaLimpia} {horaStr}";
                    }
                }
            }
            catch (Exception ex)
            {
                log.Error($"Error al consultar la última fecha y hora de sincronización para {configKey} en SAP: {ex.Message}");
            }

            return string.Empty; // Retorna vacío para activar el fallback de seguridad
        }

        private void ProcesarRespuestaZoho_Send(JObject response, string table, List<Dictionary<string, object>> recordsEnviados, string sapKeyField)
        {
            var dataArray = response["data"] as JArray;
            if (dataArray == null)
            {
                log.Warn($"Respuesta inesperada de Zoho: {response}");
                return;
            }

            if (string.IsNullOrEmpty(sapKeyField))
            {
                log.Warn($"No se definió 'SapKeyField' en la configuración de {table}. No se puede actualizar SAP.");
                return;
            }

            for (int i = 0; i < dataArray.Count; i++)
            {
                var itemResponse = dataArray[i];
                string status = itemResponse["status"]?.ToString();
                string message = itemResponse["message"]?.ToString();

                var detailsToken = itemResponse["details"];
                string detailsString = string.Empty;

                if (detailsToken != null)
                {
                    detailsString = detailsToken.Type == Newtonsoft.Json.Linq.JTokenType.Object || detailsToken.Type == Newtonsoft.Json.Linq.JTokenType.Array
                                    ? detailsToken.ToString(Newtonsoft.Json.Formatting.None) : detailsToken.ToString();
                }

                string errorCompleto = !string.IsNullOrEmpty(detailsString) ? $"{message} | Details: {detailsString}" : message;

                string sapKey = recordsEnviados[i].ContainsKey(sapKeyField) ? recordsEnviados[i][sapKeyField]?.ToString() : string.Empty;
                if (string.IsNullOrEmpty(sapKey))
                    continue;

                if (status == "success")
                {
                    string action = itemResponse["action"]?.ToString();
                    log.Info($"Éxito [{action.ToUpper()}] para el registro {sapKey}. Zoho ID: {itemResponse["details"]?["id"]}");
                    UpdateUserFieldsSAP(table, sapKey, "3");
                }
                else
                {
                    log.Error($"Error en Zoho para el registro {sapKey}: {message}");
                    UpdateUserFieldsSAP(table, sapKey, "-1", errorCompleto);
                }
            }
        }

        private List<Dictionary<string, object>> GetValuesByDocuments(string tableName, string query, string[] fields)
        {
            log.Debug("Validar configuración antes de consultar la base de datos");
            if (fields == null || fields.Length == 0)
                throw new ArgumentException($"No se han configurado campos para la consulta.");

            var itemsGroups = new List<Dictionary<string, object>>();
            log.Info(query);
            var recordSet = company.ExecuteQuery(query, log);
            log.Debug("Validar si hay registros (Cláusula de guarda)");
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
                        object value = item[cleanField];
                        string stringValue = value?.ToString() ?? string.Empty;

                        itemGroup.Add(cleanField, stringValue);
                        log.Info($"{cleanField}: {stringValue}");
                    }
                    itemsGroups.Add(itemGroup);
                }
                catch (Exception ex)
                {
                    log.Error($"Error al construir el diccionario para el registro {index} de {tableName}: {ex.Message}");
                }
                index++;
            }

            return itemsGroups;
        }

        private void UpdateUserFieldsSAP(string table, string sapKey, string status, string message = "")
        {
            Dictionary<string, object> userFields = new Dictionary<string, object>
            {
                { "U_AVS_Zoho_Sync", status },
                { "U_AVS_Zoho_ProcResult", message }
            };

            try
            {
                switch (table)
                {
                    case "OITM":
                        company.UPDATE_OITM(sapKey, userFields);
                        break;

                    default:
                        throw new Exception($"Tabla {table} no incluida para actualización de estatus.");
                }
            }
            catch (Exception ex)
            {
                log.Error($"No se pudo actualizar el estatus en SAP para {sapKey}: {ex.Message}");
            }
        }

        private async Task<JObject> PostTransactionAsync(string requestUri, string jsonPayload)
        {
            try
            {
                log.Debug("Se obtiene el token (Si ya es válido, te lo da instantáneo; si no, va a Zoho por uno nuevo)");
                string accessToken = await GetValidAccessTokenAsync();

                log.Debug("Inyectar el token en el HttpClient");
                _httpClient.DefaultRequestHeaders.Clear();
                _httpClient.DefaultRequestHeaders.Add("Authorization", $"Zoho-oauthtoken {accessToken}");

                log.Info($"Enviando solicitud POST a {requestUri}...");
                using (var content = new StringContent(jsonPayload, System.Text.Encoding.UTF8, "application/json"))
                {
                    using (HttpResponseMessage response = await _httpClient.PostAsync(requestUri, content))
                    {
                        string responseString = await response.Content.ReadAsStringAsync();
                        if (!response.IsSuccessStatusCode)
                            log.Warn($"Respuesta fallida de Zoho ({response.StatusCode}): {responseString}");

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
        #endregion

        #region Recieve

        #endregion
    }

}
