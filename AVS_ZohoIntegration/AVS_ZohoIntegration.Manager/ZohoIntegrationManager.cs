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
                var comandoUpper = comando.ToUpperInvariant();
                var isDocumentosSAP = comandoUpper.EndsWith("DOCUMENTOSSAP");
                switch (comandoUpper)
                {
                    case "DBCHECK":
                        log.Debug("Iniciando preparación de estructuras de base de datos...");
                        DbCheck();
                        break;

                    case "SENDITEMS":
                        log.Info("Iniciando proceso de envío de artículos...");
                        SendEntityToZohoAsync("OITM", "Artículos").GetAwaiter().GetResult();
                        break;

                    case "SENDPURCHASES_DOCUMENTOSSAP":
                        log.Info("Iniciando proceso de envío de pedidos a Documentos SAP...");
                        SendEntityToZohoAsync_DocumentosSAP("ORDR", "Pedidos").GetAwaiter().GetResult();
                        break;

                    case "SENDDELIVERIES_DOCUMENTOSSAP":
                        log.Info("Iniciando proceso de envío de entregas a Documentos SAP...");
                        SendEntityToZohoAsync_DocumentosSAP("ODLN", "Entregas").GetAwaiter().GetResult();
                        break;

                    case "SENDINVOICES_DOCUMENTOSSAP":
                        log.Info("Iniciando proceso de envío de facturas a Documentos SAP...");
                        SendEntityToZohoAsync_DocumentosSAP("OINV", "Facturas").GetAwaiter().GetResult();
                        break;

                    case "SENDCREDITNOTES_DOCUMENTOSSAP":
                        log.Info("Iniciando proceso de envío de notas de crédito a Documentos SAP...");
                        SendEntityToZohoAsync_DocumentosSAP("ORIN", "Notas de crédito").GetAwaiter().GetResult();
                        break;

                    case "SENDPAYMENTS_DOCUMENTOSSAP":
                        log.Info("Iniciando proceso de envío de pagos a Documentos SAP...");
                        SendEntityToZohoAsync_DocumentosSAP("ORCT", "Pagos").GetAwaiter().GetResult();
                        break;

                    case "RECEIVEPURCHASES":
                        log.Info("Iniciando proceso de recepción de pedidos...");
                        RecieveEntityFromZohoAsync("ORDR", "Pedidos").GetAwaiter().GetResult();
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
                #region OCRD
                new UDFDef
                {
                    TableName = "OCRD",
                    FieldName = "AVS_ZohoAccId",
                    Description = "Id Cuenta Zoho CRM",
                    Size = 100
                },
                #endregion

                #region OCPR
                new UDFDef
                {
                    TableName = "OCPR",
                    FieldName = "AVS_ZohoCntId",
                    Description = "Id Contacto Zoho CRM",
                    Size = 100
                },
                #endregion
                
                #region OHEM
                new UDFDef
                {
                    TableName = "OHEM",
                    FieldName = "AVS_ID_Zoho",
                    Description = "ID Zoho",
                    Size = 19
                },
                #endregion
                
                #region OITM
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
                    TableName = "OITM",
                    FieldName = "AVS_IVA",
                    Description = "IVA Zoho",
                    Size = 50
                },
                #endregion
                
                #region OINV
                new UDFDef
                {
                    TableName = "OINV",
                    FieldName = "AVS_Zoho_Sync",
                    Description = "Estatus Sincronización Zoho",
                    Size = 2,
                    ValidValues = new List<(string Value, string Description)> { ("0", "Pendiente"), ("1", "Error"), ("2", "No sincronizar"), ("3", "Sincronizado") },
                    DefaultValue = "0"
                },
                new UDFDef
                {
                    TableName = "OINV",
                    FieldName = "AVS_Zoho_ProcResult",
                    Description = "Resultado o Error de Sincronización",
                    Size = 254
                },
                new UDFDef
                {
                    TableName = "OINV",
                    FieldName = "AVS_ZohoDealId",
                    Description = "Id Trato Zoho CRM",
                    Size = 100
                },
                new UDFDef
                {
                    TableName = "OINV",
                    FieldName = "AVS_Cotizacion_Zoho",
                    Description = "Número de cotización en Zoho",
                    Size = 254
                },
                new UDFDef
                {
                    TableName = "OINV",
                    FieldName = "AVS_FormaPago_Zoho",
                    Description = "Forma de pago Zoho",
                    Size = 254
                },

                #endregion
                
                #region ORCT
                new UDFDef
                {
                    TableName = "ORCT",
                    FieldName = "AVS_Zoho_Sync",
                    Description = "Estatus Sincronización Zoho",
                    Size = 2,
                    ValidValues = new List<(string Value, string Description)> { ("0", "Pendiente"), ("1", "Error"), ("2", "No sincronizar"), ("3", "Sincronizado") },
                    DefaultValue = "0"
                },
                new UDFDef
                {
                    TableName = "ORCT",
                    FieldName = "AVS_Zoho_ProcResult",
                    Description = "Resultado o Error de Sincronización",
                    Size = 254
                },
                #endregion
                
                #region @AVS_ZOHO_LOG
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
                #endregion
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

                    case "ORDR":
                        company.UPDATE_ORDR(Convert.ToInt32(sapKey), userFields);
                        break;

                    case "ODLN":
                        company.UPDATE_ODLN(Convert.ToInt32(sapKey), userFields);
                        break;

                    case "OINV":
                        company.UPDATE_OINV(Convert.ToInt32(sapKey), userFields);
                        break;

                    case "ORIN":
                        company.UPDATE_ORIN(Convert.ToInt32(sapKey), userFields);
                        break;

                    case "ORCT":
                        company.UPDATE_ORCT(Convert.ToInt32(sapKey), userFields);
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

        private List<Dictionary<string, object>> GetValuesByDocuments(string tableName, string query, string[] fields)
        {
            log.Debug("Validar configuración antes de consultar la base de datos");
            if (fields == null || fields.Length == 0)
                throw new ArgumentException($"No se han configurado campos para la consulta.");

            var itemsGroups = new List<Dictionary<string, object>>();
            log.Debug(query);
            var recordSet = company.ExecuteQuery(query);
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

        #endregion

        #region Send
        private async Task SendEntityToZohoAsync(string configKey, string entityDescription)
        {
            if (!company.Zoho_EntityConfig.TryGetValue(configKey, out ZohoDocumentConfig docConfig) || docConfig.SapToZoho == null)
            {
                log.Error($"La configuración SAP_TO_ZOHO para '{configKey}' no existe en el JSON.");
                return;
            }

            ZohoEntityConfig entityConfig = docConfig.SapToZoho;

            #region Validación de nodo principal
            log.Info("Obteniendo ultima fecha de sincronización");
            string lastSyncDate = ObtenerUltimaFechaSincronizacionDesdeSAP(configKey);
            if (string.IsNullOrEmpty(lastSyncDate))
            {
                lastSyncDate = DateTime.Now.AddDays(-1).ToString("yyyy-MM-dd HH:mm:ss");
                log.Warn($"No se encontró fecha de última sincronización para {configKey}. Aplicando fallback de seguridad: {lastSyncDate}");
            }
            else
                log.Info($"Última sincronización exitosa registrada para {configKey}: {lastSyncDate}");

            string query = entityConfig.Query?.Replace("@LastSyncDate", $"'{lastSyncDate}'");
            string[] fields = entityConfig.Fields?.Split(',') ?? new string[0];
            string apiEndpoint = entityConfig.API;

            log.Info($"*** Obteniendo información de cabecera: {entityDescription} ({configKey}) ***");

            var records = GetValuesByDocuments(configKey, query, fields);
            if (records == null || !records.Any())
            {
                log.Info($"** No hay registros pendientes de {configKey} para procesar.");
                return;
            }
            #endregion

            #region Validación de subnodos
            if (entityConfig.DetailNodes != null && entityConfig.DetailNodes.Any())
            {
                foreach (var node in entityConfig.DetailNodes)
                {
                    string cleanNodeName = node.Key.Trim();
                    ZohoDetailNodeConfig detailConfig = node.Value;
                    log.Info($"* Consultando sub-entidad [{cleanNodeName}] para {configKey}...");

                    string detailQueryTemplate = detailConfig.Query;
                    string[] detailFields = detailConfig.Fields?.Split(',') ?? new string[0];
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
            #endregion

            #region Formato a Json
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
            log.Debug(jsonPayload);
            log.Info($"** {records.Count} registros listos para Upsert. Enviando a Zoho...");

            #endregion

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
                log.Debug(query);
                var recordSet = company.ExecuteQuery(query);

                if (recordSet != null && recordSet.Count > 0)
                {
                    var fechaStr = recordSet[0]["U_LastSync"]?.ToString();
                    var horaStr = recordSet[0]["U_LastTime"]?.ToString();

                    if (!string.IsNullOrEmpty(fechaStr))
                    {
                        DateTime parsedDate = Convert.ToDateTime(fechaStr);
                        string fechaLimpia = parsedDate.ToString("yyyy-MM-dd");

                        // Normalización segura de la hora para evitar formatos inválidos como "1650"
                        string horaLimpia = "00:00:00";

                        if (!string.IsNullOrEmpty(horaStr))
                        {
                            horaStr = horaStr.Trim();

                            // Si SAP devuelve la hora en formato corto sin dos puntos (ej. "1650")
                            if (horaStr.Length == 4 && !horaStr.Contains(":"))
                            {
                                horaLimpia = $"{horaStr.Substring(0, 2)}:{horaStr.Substring(2, 2)}:00";
                            }
                            // Si viene completa sin dos puntos (ej. "165030")
                            else if (horaStr.Length == 6 && !horaStr.Contains(":"))
                            {
                                horaLimpia = $"{horaStr.Substring(0, 2)}:{horaStr.Substring(2, 2)}:{horaStr.Substring(4, 2)}";
                            }
                            // Si ya viene con formato de hora estándar, intentamos parsearla
                            else if (TimeSpan.TryParse(horaStr, out var parsedTime))
                            {
                                horaLimpia = parsedTime.ToString(@"hh\:mm\:ss");
                            }
                            else
                            {
                                horaLimpia = horaStr; // Fallback si ya viene correcta
                            }
                        }

                        return $"{fechaLimpia} {horaLimpia}";
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

        #endregion

        #region Send_DocumentosSAP
        private async Task SendEntityToZohoAsync_DocumentosSAP(string configKey, string entityDescription)
        {
            if (!company.Zoho_EntityConfig.TryGetValue(configKey, out ZohoDocumentConfig docConfig) || docConfig.SapToZoho == null)
            {
                log.Error($"La configuración SAP_TO_ZOHO para '{configKey}' no existe en el JSON.");
                return;
            }

            ZohoEntityConfig entityConfig = docConfig.SapToZoho;

            #region Validación de nodo principal (Cabeceras)
            string query = entityConfig.Query;
            string[] fields = entityConfig.Fields?.Split(',') ?? new string[0];
            string apiEndpoint = entityConfig.API;

            log.Info($"*** Obteniendo información de cabecera: {entityDescription} ({configKey}) ***");
            log.Info($"Ejecutando consulta principal en SAP para '{configKey}' con {fields.Length} campos mapeados.");

            var records = GetValuesByDocuments(configKey, query, fields);
            if (records == null || !records.Any())
            {
                log.Info($"** No hay registros pendientes de {configKey} para procesar en SAP.");
                return;
            }

            log.Info($"Se obtuvieron exitosamente {records.Count} registros de cabecera para '{configKey}'.");
            #endregion

            #region Validación de subnodos y archivos PDF
            if (entityConfig.DetailNodes != null && entityConfig.DetailNodes.Any())
            {
                log.Info($"Iniciando procesamiento de {entityConfig.DetailNodes.Count} subnodo(s) configurado(s) para '{configKey}'.");

                foreach (var node in entityConfig.DetailNodes)
                {
                    string cleanNodeName = node.Key.Trim();
                    ZohoDetailNodeConfig detailConfig = node.Value;
                    log.Info($"* Consultando sub-entidad [{cleanNodeName}] para {configKey}...");

                    string detailQueryTemplate = detailConfig.Query;
                    string[] detailFields = detailConfig.Fields?.Split(',') ?? new string[0];
                    string detailKey = detailConfig.DetailKey;
                    string arrayName = detailConfig.DetailArrayName;
                    bool isSingleObject = detailConfig.IsSingleObject;

                    var keysToSearch = records
                        .Where(r => r.ContainsKey(detailKey) && r[detailKey] != null)
                        .Select(r => $"'{r[detailKey]}'")
                        .Distinct()
                        .ToList();

                    if (!keysToSearch.Any())
                    {
                        log.Info($"No se encontraron llaves de enlace '{detailKey}' para el subnodo [{cleanNodeName}]. Se omite esta sub-entidad.");
                        continue;
                    }

                    log.Info($"Se recolectaron {keysToSearch.Count} llaves únicas para consultar el detalle de [{cleanNodeName}].");

                    string inClauseValues = string.Join(",", keysToSearch);
                    string finalDetailQuery = detailQueryTemplate.Replace("@InClause", inClauseValues);

                    var detailRecords = GetValuesByDocuments($"{configKey}_{cleanNodeName}", finalDetailQuery, detailFields);
                    if (detailRecords != null && detailRecords.Any())
                    {
                        log.Info($"Se obtuvieron {detailRecords.Count} registros crudos para el subnodo [{cleanNodeName}]. Iniciando validaciones y adjuntos...");

                        var validDetailRecords = new List<Dictionary<string, object>>();

                        foreach (var detail in detailRecords)
                        {
                            string subnodeSapKey = detail.ContainsKey("DocEntry") ? detail["DocEntry"]?.ToString() : string.Empty;
                            bool isValid = true;

                            if (detail.ContainsKey("Adjunto") && detail["Adjunto"] != null)
                            {
                                string filePath = detail["Adjunto"].ToString().Trim();
                                if (!string.IsNullOrEmpty(filePath) && !System.IO.File.Exists(filePath))
                                {
                                    log.Warn($"El archivo adjunto del subnodo no existe en disco: '{filePath}'. Registro SAP Key: {subnodeSapKey}");

                                    if (!string.IsNullOrEmpty(subnodeSapKey))
                                    {
                                        log.Info($"Actualizando campos de usuario en SAP (ORDR) con estatus de Error por archivo ausente para DocEntry: {subnodeSapKey}");
                                        UpdateUserFieldsSAP("ORDR", subnodeSapKey, "Error", $"El archivo adjunto no existe en la ruta: {filePath}");
                                    }

                                    isValid = false;
                                }
                            }

                            if (isValid)
                                validDetailRecords.Add(detail);
                        }

                        detailRecords = validDetailRecords;
                        log.Info($"Quedaron {detailRecords.Count} registros válidos para el subnodo [{cleanNodeName}] tras validar archivos adjuntos.");

                        foreach (var header in records)
                        {
                            string keyValue = header[detailKey]?.ToString();

                            var matchingDetails = new List<Dictionary<string, object>>();
                            var subnodosFiltrados = detailRecords
                                .Where(d => d.ContainsKey(detailKey) && d[detailKey]?.ToString() == keyValue)
                                .ToList();

                            foreach (var d in subnodosFiltrados)
                            {
                                var cleanDetail = new Dictionary<string, object>(d);
                                cleanDetail.Remove(detailKey);

                                if (cleanDetail.ContainsKey("Adjunto") && cleanDetail["Adjunto"] != null)
                                {
                                    string filePath = cleanDetail["Adjunto"].ToString().Trim();

                                    if (!string.IsNullOrEmpty(filePath) && System.IO.File.Exists(filePath))
                                    {
                                        log.Info($"Subiendo PDF temporalmente a Zoho Files desde la ruta: {filePath}");
                                        string fileId = await SubirArchivoObtenerIdAsync(filePath);
                                        if (!string.IsNullOrEmpty(fileId))
                                        {
                                            log.Info($"Archivo subido con éxito a Zoho. File ID obtenido: {fileId}");
                                            var fileAttachment = new List<Dictionary<string, string>>
                                            {
                                                new Dictionary<string, string> { { "file_id", fileId } }
                                            };

                                            cleanDetail.Add("Adjunt1", fileAttachment);
                                        }
                                        else
                                            log.Warn($"El método SubirArchivoObtenerIdAsync no devolvió un File ID válido para el archivo: {filePath}");
                                    }

                                    cleanDetail.Remove("Adjunto");
                                }

                                string[] camposComoTexto = { "No_Doc", "Adjunt1" };

                                var normalizedDetail = cleanDetail.ToDictionary(
                                    kvp => kvp.Key,
                                    kvp =>
                                    {
                                        var val = kvp.Value;
                                        if (val == null)
                                            return null;

                                        if (DateTime.TryParse(val.ToString(), out DateTime parsedDate))
                                            return parsedDate.ToString("yyyy-MM-dd");

                                        string strVal = val.ToString().Trim();

                                        if (camposComoTexto.Contains(kvp.Key, StringComparer.OrdinalIgnoreCase))
                                            return strVal;

                                        if (strVal.Equals("true", StringComparison.OrdinalIgnoreCase) || strVal.Equals("false", StringComparison.OrdinalIgnoreCase))
                                            return bool.Parse(strVal);

                                        if (strVal.Equals("Y", StringComparison.OrdinalIgnoreCase) || strVal.Equals("N", StringComparison.OrdinalIgnoreCase))
                                            return strVal.Equals("Y", StringComparison.OrdinalIgnoreCase);

                                        if (int.TryParse(strVal, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out int intVal))
                                            return intVal;

                                        if (long.TryParse(strVal, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out long longVal))
                                            return longVal;

                                        if (decimal.TryParse(strVal, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out decimal decVal))
                                            return decVal;

                                        return val;
                                    }
                                );

                                matchingDetails.Add(normalizedDetail);
                            }

                            if (isSingleObject)
                                header.Add(arrayName, matchingDetails.FirstOrDefault());
                            else
                                header.Add(arrayName, matchingDetails);
                        }
                    }
                    else
                    {
                        log.Info($"No se encontraron registros de detalle para el subnodo [{cleanNodeName}].");
                    }
                }
            }
            #endregion

            #region Formato a Json del nodo principal
            log.Info($"Normalizando registros de cabecera y removiendo 'DocEntry' interno para la serialización final.");
            var normalizedRecords = records.Select(record =>
            {
                var cleanRecord = new Dictionary<string, object>(record);
                cleanRecord.Remove("DocEntry");
                return cleanRecord;
            }).ToList();

            var apiPayload = new { data = normalizedRecords };
            string jsonPayload = JsonConvert.SerializeObject(apiPayload);

            log.Debug($"Payload JSON generado para Zoho: {jsonPayload}");
            log.Info($"** {records.Count} registros listos para Upsert. Preparando envío HTTP hacia Zoho...");
            #endregion

            try
            {
                string upsertEndpoint = apiEndpoint.EndsWith("/upsert", StringComparison.OrdinalIgnoreCase) ? apiEndpoint : apiEndpoint.TrimEnd('/') + "/upsert";
                log.Info($"Endpoint final construido para Upsert en Zoho: {upsertEndpoint}");

                jsonPayload = jsonPayload.Replace("Documentos_SAP", "Informaci_n_de_Doc_SAP");

                JObject response = await PostTransactionAsync(upsertEndpoint, jsonPayload);
                log.Info($"Petición PostTransactionAsync completada para '{configKey}'. Procesando respuesta de Zoho...");

                ProcesarRespuestaZoho_Send(response, configKey, records, entityConfig.SapKeyField);
                log.Info($"*** Proceso de envío para '{configKey}' finalizado correctamente. ***");
            }
            catch (Exception ex)
            {
                log.Error($"**** Excepción crítica al enviar {configKey} a Zoho ****", ex);
            }
        }

        private async Task<string> SubirArchivoObtenerIdAsync(string filePath)
        {
            try
            {
                using (var client = new HttpClient())
                {
                    string accessToken = await GetValidAccessTokenAsync(); // Asegúrate de llamar a tu método de token
                    client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);

                    using (var form = new MultipartFormDataContent())
                    {
                        byte[] fileBytes = System.IO.File.ReadAllBytes(filePath);
                        string fileName = System.IO.Path.GetFileName(filePath);

                        var fileContent = new ByteArrayContent(fileBytes);
                        fileContent.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse("application/pdf");
                        form.Add(fileContent, "file", fileName);

                        string filesApiUrl = "https://www.zohoapis.com/crm/v8/files";
                        HttpResponseMessage response = await client.PostAsync(filesApiUrl, form);

                        if (response.IsSuccessStatusCode)
                        {
                            string jsonResponse = await response.Content.ReadAsStringAsync();
                            JObject responseObj = JObject.Parse(jsonResponse);

                            var dataArray = responseObj["data"] as JArray;
                            if (dataArray != null && dataArray.Count > 0)
                            {
                                string status = dataArray[0]["status"]?.ToString();
                                if (status.Equals("success", StringComparison.OrdinalIgnoreCase))
                                {
                                    return dataArray[0]["details"]?["id"]?.ToString();
                                }
                            }
                        }
                        else
                        {
                            string errorResponse = await response.Content.ReadAsStringAsync();
                            log.Error($"Error HTTP {response.StatusCode} al subir archivo a Zoho Files: {errorResponse}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                log.Error($"Excepción al intentar subir archivo a Zoho: {ex.Message}");
            }
            return null;
        }
        #endregion

        #region Recieve
        private async Task RecieveEntityFromZohoAsync(string configKey, string entityDescription)
        {
            if (!company.Zoho_EntityConfig.TryGetValue(configKey, out ZohoDocumentConfig docConfig) || docConfig.ZohoToSap == null)
            {
                log.Error($"La configuración ZOHO_TO_SAP para '{configKey}' no existe en el JSON.");
                return;
            }

            ZohoEntityConfig entityConfig = docConfig.ZohoToSap;
            log.Info($"*** Procesando integración dinámica: {entityDescription} ({configKey}) ***");

            string apiEndpoint = entityConfig.API;
            if (string.IsNullOrWhiteSpace(apiEndpoint))
            {
                log.Error($"La ruta de la API para la recepción de '{configKey}' está vacía.");
                return;
            }

            try
            {
                log.Info($"Iniciando petición HTTP hacia Zoho en el endpoint: {apiEndpoint}");
                JObject response = await GetTransactionAsync(apiEndpoint);
                if (response == null || !response.ContainsKey("data"))
                {
                    log.Info("No se encontraron datos para procesar en la respuesta de Zoho (la clave 'data' está ausente o es nula).");
                    return;
                }

                JArray quotes = (JArray)response["data"];
                log.Info($"Se han recuperado {quotes.Count} cotizaciones para evaluar desde Zoho.");

                int procesadasExito = 0;
                int procesadasError = 0;

                foreach (JObject quote in quotes)
                {
                    string quoteId = quote["id"]?.ToString();
                    string sapCardCode = string.Empty;

                    log.Info($"--------------------------------------------------war");
                    log.Info($"Iniciando procesamiento de la cotización con ID de Zoho: {quoteId}");

                    try
                    {
                        if (string.IsNullOrEmpty(quoteId))
                            throw new Exception("La cotización no contiene un ID válido.");

                        #region Recuperar datos de SN
                        JToken accountToken = quote["Account_Name"];
                        if (accountToken == null || accountToken.Type == JTokenType.Null)
                            throw new Exception("La cotización no tiene una cuenta asociada.");

                        string zohoAccountId = accountToken["id"]?.ToString();
                        log.Info($"Cotización {quoteId} vinculada a la cuenta de Zoho ID: {zohoAccountId}");

                        string accountEndpoint = $"https://www.zohoapis.com/crm/v8/Accounts/{zohoAccountId}";
                        log.Info($"Consultando detalles de la cuenta en Zoho: {accountEndpoint}");

                        JObject accountResponse = await GetTransactionAsync(accountEndpoint);
                        if (accountResponse == null || !accountResponse.ContainsKey("data"))
                            throw new Exception($"No se pudo recuperar la cuenta {zohoAccountId} desde Zoho.");

                        JObject fullAccountData = (JObject)accountResponse["data"][0];
                        var rfc = string.Empty;
                        if (fullAccountData.ContainsKey("RFC"))
                            rfc = fullAccountData["RFC"]?.ToString();
                        else if (fullAccountData.ContainsKey("MIT"))
                            rfc = fullAccountData["MIT"]?.ToString();

                        if (string.IsNullOrWhiteSpace(rfc))
                            throw new Exception($"La cuenta {zohoAccountId} no tiene configurado un RFC válido.");

                        log.Info($"Cuenta {zohoAccountId} obtenida correctamente. RFC identificado: {rfc}");

                        string requestedFields = "id,Owner,Lead_Source,First_Name,Last_Name,Account_Name,Vendor_Name,Email,Department,Phone,Mobile,Created_By,Modified_By,Created_Time,Modified_Time,Full_Name,Mailing_Street,Mailing_City,Mailing_Zip,Mailing_Country,Description,Email_Opt_Out,Salutation,Last_Activity_Time,Tag,Record_Image,Reporting_To,Unsubscribed_Mode,Unsubscribed_Time,Change_Log_Time__s,Locked__s,Last_Enriched_Time__s,Enrich_Status__s,Last_Visited_Time,First_Visited_URL,Average_Time_Spent_Minutes,Number_Of_Chats,Referrer,Visitor_Score,First_Visited_Time,Days_Visited,Estado_MX,Puesto_o_Cargo";
                        string contactsEndpoint = $"https://www.zohoapis.com/crm/v8/Contacts/search?criteria=(Account_Name.id:equals:{zohoAccountId})&fields={requestedFields}";

                        log.Info($"Buscando contactos asociados a la cuenta {zohoAccountId} en Zoho...");
                        JObject contactsResponse = await GetTransactionAsync(contactsEndpoint);
                        JArray zohoContacts = contactsResponse != null && contactsResponse.ContainsKey("data") ? (JArray)contactsResponse["data"] : new JArray();

                        log.Info($"Se encontraron {zohoContacts.Count} contactos asociados en Zoho para la cuenta {zohoAccountId}.");
                        #endregion

                        company.StartTransaction();

                        try
                        {
                            #region Crear/actualizar SN
                            log.Info($"Verificando existencia del Socio de Negocios en SAP mediante RFC: {rfc}");
                            sapCardCode = ObtenerCardCodePorRfc(rfc);
                            bool existeEnSap = !string.IsNullOrEmpty(sapCardCode);
                            if (existeEnSap)
                                log.Info($"El Socio de Negocios ya existe en SAP. CardCode asociado: {sapCardCode}. Procediendo a actualizar...");
                            else
                                log.Info($"El Socio de Negocios NO existe en SAP para el RFC {rfc}. Procediendo a crear uno nuevo...");

                            company.ProcesarSocioNegocioDIAPI(fullAccountData, zohoContacts, sapCardCode, rfc, existeEnSap, log);

                            if (string.IsNullOrEmpty(sapCardCode))
                            {
                                sapCardCode = company.Get_NewObjectKey();
                                log.Info($"Socio de Negocios creado exitosamente. Nuevo CardCode asignado por SAP: {sapCardCode}");
                            }
                            else
                                log.Info($"Socio de Negocios {sapCardCode} actualizado exitosamente en SAP.");
                            #endregion

                            #region Recuperar datos completos de la cotización

                            string fullQuoteEndpoint = $"/crm/v8/Quotes/{quoteId}";
                            log.Info($"Obteniendo detalle completo (líneas/partidas) de la cotización {quoteId} desde Zoho...");

                            JObject fullQuoteResponse = await GetTransactionAsync(fullQuoteEndpoint);
                            if (fullQuoteResponse == null || !fullQuoteResponse.ContainsKey("data"))
                                throw new Exception("No se pudo obtener el detalle (líneas) de la cotización desde Zoho.");

                            JObject fullQuoteData = (JObject)fullQuoteResponse["data"][0];
                            log.Info($"Detalle de cotización {quoteId} obtenido. Procediendo a crear la Orden de Venta en SAP para el cliente {sapCardCode}...");
                            #endregion

                            #region Crear orden de venta
                            company.ProcesarOrdenVentaDIAPI(fullQuoteData, sapCardCode, log);
                            #endregion

                            company.TransactionCommit();
                        }
                        catch (Exception exSap)
                        {
                            company.TransactionRollBack();
                            throw new Exception($"Error en transacción de SAP: {exSap.Message}", exSap);
                        }

                        await GuardarEstatusSincronizacionAsync(quoteId, 3, "Sincronización exitosa", sapCardCode);
                        log.Info($"¡Cotización {quoteId} procesada y sincronizada correctamente como Orden de Venta en SAP!");
                        procesadasExito++;
                    }
                    catch (Exception ex)
                    {
                        procesadasError++;
                        log.Error($"[ERROR DE PROCESAMIENTO] Falló la cotización {quoteId}: {ex.Message}");

                        if (!string.IsNullOrEmpty(quoteId))
                        {
                            log.Info($"Actualizando estatus de error (1) en Zoho para la cotización {quoteId}...");
                            await GuardarEstatusSincronizacionAsync(quoteId, 1, ex.Message, sapCardCode);
                        }
                    }
                }

                log.Info($"*** Fin del ciclo de sincronización. Resumen - Éxitos: {procesadasExito} | Errores: {procesadasError} ***");
                log.Info("Información de Zoho recibida y procesada con éxito.");
            }
            catch (Exception ex)
            {
                log.Error($"**** Excepción crítica al recibir {configKey} desde Zoho ****", ex);
            }
        }

        private async Task<JObject> GetTransactionAsync(string requestUri)
        {
            try
            {
                log.Debug("Se obtiene el token (Si ya es válido, te lo da instantáneo; si no, va a Zoho por uno nuevo)");
                string accessToken = await GetValidAccessTokenAsync();

                log.Debug("Inyectar el token en el HttpClient");
                _httpClient.DefaultRequestHeaders.Clear();
                _httpClient.DefaultRequestHeaders.Add("Authorization", $"Zoho-oauthtoken {accessToken}");

                log.Info($"Enviando solicitud POST a {requestUri}...");
                using (HttpResponseMessage response = await _httpClient.GetAsync(requestUri))
                {
                    string responseString = await response.Content.ReadAsStringAsync();
                    if (!response.IsSuccessStatusCode)
                        log.Warn($"Respuesta fallida de Zoho ({response.StatusCode}): {responseString}");

                    return JObject.Parse(responseString);
                }
            }
            catch (Exception ex)
            {
                log.Error($"Error de red al consumir API POST {requestUri}: {ex.Message}");
                throw;
            }
        }

        private string ObtenerCardCodePorRfc(string rfc)
        {
            try
            {
                string query = $"SELECT \"CardCode\" FROM OCRD WHERE \"LicTradNum\" = '{rfc}' AND \"CardType\" = 'C'";
                var recordSet = company.ExecuteQuery(query);
                var primerRegistro = recordSet?.FirstOrDefault();
                return primerRegistro != null && primerRegistro.ContainsKey("CardCode") ? primerRegistro["CardCode"]?.ToString() : string.Empty;
            }
            catch (Exception ex)
            {
                log.Error($"Error consultando RFC {rfc} mediante DI API", ex);
                return string.Empty;
            }
        }

        private async Task GuardarEstatusSincronizacionAsync(string idCotizacion, int estatus, string detalle, string sapCardCode)
        {
            log.Info($"[ESTATUS CONTROL] Quote ID: {idCotizacion} | Estatus: {estatus} | Detalle: {detalle} | CardCode {sapCardCode}");

            try
            {
                var payloadObj = new
                {
                    data = new[]
                    {
                        new
                        {
                            id = idCotizacion,
                            ID_SAP = sapCardCode,
                            Estatus_Sincronizaci_n = estatus,
                            Detalle_Error = detalle.Length > 255 ? detalle.Substring(0, 255) : detalle
                        }
                    }
                };

                string jsonPayload = Newtonsoft.Json.JsonConvert.SerializeObject(payloadObj);
                var content = new StringContent(jsonPayload, System.Text.Encoding.UTF8, "application/json");

                string upsertEndpoint = "https://www.zohoapis.com/crm/v8/Quotes/upsert";
                JObject response = await PostTransactionAsync(upsertEndpoint, jsonPayload);

            }
            catch (Exception ex)
            {
                log.Error($"Excepción al intentar actualizar el estatus en Zoho de la cotización {idCotizacion}.", ex);
            }
        }
        #endregion
    }

}
