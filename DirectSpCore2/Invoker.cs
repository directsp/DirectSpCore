﻿using DirectSp.Core.Entities;
using DirectSp.Core.Exceptions;
using DirectSp.Core.ProcedureInfos;
using DirectSp.Core.Providers;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DirectSp.Core
{
    public class Invoker
    {
        private class FieldInfo
        {
            public string Name { get; set; }
            public string TypeName { get; set; }
            public SpFieldInfo ExtendedProps { get; set; }
        }

        public IKeyValueProvider KeyValueProvider { get; }
        public InvokerPath InvokerPath { get; }
        private readonly CaptchaController _captchaController;
        private readonly ICommandProvider _commandProvider;
        private readonly string _schema;
        private readonly UserSessionManager _sessionManager;
        private readonly ILogger _logger;
        private readonly JwtTokenSigner _tokenSigner;
        private readonly bool _useCamelCase;
        private readonly int _sessionMaxRequestCount;
        private readonly double _sessionMaxRequestCycleInterval;
        private readonly bool _isDownloadEnabled;
        private readonly double _readonlyConnectionSyncInterval;
        private readonly int _downloadedRecordsetFileLifetime;
        private readonly CultureInfo _alternativeCulture;
        private readonly AlternativeCalendar _alternativeCalendar;

        private readonly object LockObject = new object();

        public Invoker(InvokerOptions options)
        {
            if (options.CommandProvider == null) throw new ArgumentNullException("CommandProvider");

            InvokerPath = new InvokerPath(options.WorkspaceFolderPath);
            KeyValueProvider = options.KeyValueProvider ?? new MemoryKeyValueProvder();
            _logger = options.Logger;
            _schema = options.Schema ?? throw new Exception("Schema is not set!");
            _sessionManager = new UserSessionManager(options.SessionTimeout);
            _sessionMaxRequestCycleInterval = options.SessionMaxRequestCycleInterval;
            _sessionMaxRequestCount = options.SessionMaxRequestCount;
            _tokenSigner = new JwtTokenSigner(options.CertificateProvider);
            _readonlyConnectionSyncInterval = options.ReadonlyConnectionSyncInterval;
            _useCamelCase = options.UseCamelCase;
            _isDownloadEnabled = options.IsDownloadEnabled;
            _downloadedRecordsetFileLifetime = options.DownloadedRecordsetFileLifetime;
            _captchaController = new CaptchaController(KeyValueProvider, options.CaptchaProvider);
            _alternativeCulture = options.AlternativeCulture;
            _commandProvider = options.CommandProvider;
            _alternativeCalendar = new AlternativeCalendar(_alternativeCulture);
            SpException.UseCamelCase = options.UseCamelCase;
        }

        private DateTime? LastCleanTempFolderTime;

        public InvokeContext _appUserContext;
        public InvokeContext AppUserContext
        {
            get
            {
                lock (LockObject)
                {
                    if (_appUserContext == null)
                    {
                        RefreshApi();
                    }

                    return _appUserContext;
                }
            }
        }

        private Dictionary<string, SpInfo> _SpInfos;
        public Dictionary<string, SpInfo> SpInfos
        {
            get
            {
                lock (LockObject)
                {
                    if (_SpInfos == null)
                    {
                        RefreshApi();
                    }

                    return _SpInfos;
                }
            }
        }

        public string AppName => AppUserContext.AppName;
        public string AppVersion { get; private set; }

        // User Request Count control
        private void VerifyUserRequestLimit(UserSession userSession)
        {
            //AppUserId does not have request limit
            if (userSession.SpContext.AuthUserId == AppUserContext.AuthUserId)
            {
                return;
            }

            //Reset ResetRequestCount
            if (userSession.RequestIntervalStartTime.AddSeconds(_sessionMaxRequestCycleInterval) < DateTime.Now)
            {
                userSession.ResetRequestCount();
            }

            //Reject Request
            if (userSession.RequestCount > _sessionMaxRequestCount)
            {
                throw new SpException("Too many request! Please try a few minutes later!", SpException.Status429TooManyRequests);
            }
        }

        private void RefreshApi()
        {
            lock (LockObject)
            {
                var spInfos = new Dictionary<string, SpInfo>();
                {
                    var spList = _commandProvider.GetSystemApi(out string appUserContext).Result;
                    foreach (var item in spList)
                        spInfos.Add(item.SchemaName + "." + item.ProcedureName, item);

                    _SpInfos = spInfos;
                    _appUserContext = new InvokeContext(appUserContext, "$$");
                    AppVersion = _appUserContext.AppVersion; //don't make AppVersion property because _AppUserContext may not be initialized when there is error
                }
            }
        }

        public async Task<SpCallResult[]> Invoke(SpCall[] spCalls, SpInvokeParams spInvokeParams)
        {
            //Check DuplicateRequest if spCalls contian at least one write
            foreach (var spCall in spCalls)
            {
                var spInfo = FindSpInfo($"{_schema}.{spCall.Method}");
                if (spInfo != null && spInfo.ExtendedProps.DataAccessMode == SpDataAccessMode.Write)
                {
                    await CheckDuplicateRequest(spInvokeParams.InvokeOptions.RequestId, 3600 * 2);
                    break;
                }
            }

            var spi = new SpInvokeParamsInternal
            {
                SpInvokeParams = spInvokeParams,
                IsBatch = true
            };

            var spCallResults = new List<SpCallResult>();
            var tasks = new List<Task<SpCallResult>>();
            foreach (var spCall in spCalls)
            {
                tasks.Add(Invoke(spCall, spi));
            }

            try
            {
                await Task.WhenAll(tasks.ToArray());
            }
            catch
            {
                // catch await single exception
            }

            foreach (var item in tasks)
            {
                if (item.IsCompleted)
                    spCallResults.Add(item.Result);
                else
                    spCallResults.Add(new SpCallResult { { "error", SpExceptionAdapter.Convert(item.Exception.InnerException, _captchaController).SpCallError } });
            }

            return spCallResults.ToArray();
        }


        public async Task<SpCallResult> Invoke(SpCall spCall)
        {
            var spInvokeParams = new SpInvokeParams();
            return await Invoke(spCall, spInvokeParams, true);
        }

        public async Task<SpCallResult> Invoke(string method, object param)
        {
            var spInvokeParams = new SpInvokeParams();
            return await Invoke(method, param, spInvokeParams, true);
        }

        public async Task<SpCallResult> Invoke(string method, object param, SpInvokeParams spInvokeParams, bool isSystem = false)
        {
            // Create spCall
            var spCall = new SpCall
            {
                Method = method
            };

            foreach (var propInfo in param.GetType().GetProperties())
                spCall.Params.Add(propInfo.Name, propInfo.GetValue(param));

            return await Invoke(spCall, spInvokeParams, isSystem);
        }

        public async Task<SpCallResult> Invoke(SpCall spCall, SpInvokeParams spInvokeParams, bool isSystem = false)
        {
            // Check duplicate request
            var spInfo = FindSpInfo($"{_schema}.{spCall.Method}");
            if (spInfo != null && spInfo.ExtendedProps.DataAccessMode == SpDataAccessMode.Write)
                await CheckDuplicateRequest(spInvokeParams.InvokeOptions.RequestId, spInfo.ExtendedProps.CommandTimeout);

            // Call main invoke
            var spi = new SpInvokeParamsInternal { SpInvokeParams = spInvokeParams, IsSystem = isSystem };
            if (isSystem)
                spi.SpInvokeParams.AuthUserId = AppUserContext.AuthUserId;

            return await Invoke(spCall, spi);
        }

        private async Task<SpCallResult> Invoke(SpCall spCall, SpInvokeParamsInternal spi)
        {
            try
            {
                return await InvokeCore(spCall, spi);
            }
            catch (SpInvokerAppVersionException)
            {
                RefreshApi();
                return await Invoke(spCall, spi);
            }
            catch (SpException spException) //catch any read-only errors
            {
                throw spException.SpCallError.ErrorNumber == 3906 ? new SpMaintenanceReadOnlyException(spCall.Method) : spException;
            }
        }

        private async Task<SpCallResult> InvokeCore(SpCall spCall, SpInvokeParamsInternal spi)
        {
            try
            {
                // Check captcha
                await CheckCaptcha(spCall, spi);

                // Call core
                var result = await InvokeCore2(spCall, spi);

                // Update result
                await UpdateRecodsetDownloadUri(spCall, spi, result);

                return result;

            }
            catch (Exception ex)
            {
                throw SpExceptionAdapter.Convert(ex, _captchaController);
            }
        }

        private async Task<SpCallResult> InvokeCore2(SpCall spCall, SpInvokeParamsInternal spi)
        {
            if (!spi.IsSystem && string.IsNullOrWhiteSpace(spi.SpInvokeParams.UserRemoteIp))
            {
                var ex = new ArgumentException(spi.SpInvokeParams.UserRemoteIp, "UserRemoteIp");
                _logger?.LogError(ex.Message, ex);
                throw ex;
            }

            // retrieve user session
            var invokeParams = spi.SpInvokeParams;
            var invokeOptions = spi.SpInvokeParams.InvokeOptions;
            var userSession = _sessionManager.GetUserSession(AppName, invokeParams.AuthUserId, invokeParams.Audience);

            //Verify user request limit
            VerifyUserRequestLimit(userSession);

            //call the sp
            var spName = _schema + "." + spCall.Method;
            var spInfo = FindSpInfo(spName);
            if (spInfo == null)
            {
                var ex = new SpException($"Could not find the API: {spName}");
                _logger?.LogWarning(ex.Message, ex);//Log exception
                throw ex;
            }

            //check IsCaptcha by meta-data
            if ((spInfo.ExtendedProps.CaptchaMode == SpCaptchaMode.Always || spInfo.ExtendedProps.CaptchaMode == SpCaptchaMode.Auto) && !spi.IsCaptcha)
                throw new SpInvalidCaptchaException(await _captchaController.Create(), spInfo.ProcedureName);

            //check IsBatchAllowed by meta-data
            if (!spInfo.ExtendedProps.IsBatchAllowed && spi.IsBatch)
                throw new SpBatchIsNotAllowedException(spInfo.ProcedureName);

            //Create spCallOptions
            var spCallOptions = new SpCallOptions()
            {
                IsBatch = spi.IsBatch,
                IsCaptcha = spi.IsCaptcha,
                RecordIndex = invokeOptions.RecordIndex,
                RecordCount = invokeOptions.RecordCount,
                InvokerAppVersion = AppVersion,
                IsReadonlyIntent = spInfo.ExtendedProps.DataAccessMode == SpDataAccessMode.Read || spInfo.ExtendedProps.DataAccessMode == SpDataAccessMode.ReadSnapshot
            };

            //Get Connection String caring about ReadScale
            var isReadScale = IsReadScale(spInfo, userSession, spi, out bool isWriteMode);

            //create SqlParameters
            var spCallResults = new SpCallResult();
            var paramValues = new Dictionary<string, object>();

            //set context param if exists
            var contextSpParam = spInfo.Params.FirstOrDefault(x => x.ParamName.Equals("context", StringComparison.InvariantCultureIgnoreCase));
            if (contextSpParam != null)
                paramValues.Add(contextSpParam.ParamName, userSession.SpContext.ToString(spCallOptions));

            //set other caller params
            var spCallParams = spCall.Params ?? new Dictionary<string, object>();
            foreach (var spCallparam in spCallParams)
            {
                var paramName = spCallparam.Key;
                var paramValue = spCallparam.Value;

                //find sqlParam for callerParam
                var spParam = spInfo.Params.FirstOrDefault(x => x.ParamName.Equals($"{paramName}", StringComparison.OrdinalIgnoreCase));
                if (spParam == null)
                    throw new ArgumentException($"parameter '{paramName}' does not exists!");
                spInfo.ExtendedProps.Params.TryGetValue(spParam.ParamName, out SpParamInfoEx spParamEx);

                //make sure Context has not been set be the caller
                if (paramName.Equals("Context", StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException($"You can not set '{paramName}' parameter!");

                // extract data from actual signed data
                var value = CheckJwt(paramValue, spParam, spParamEx);

                // convert data for db
                paramValue = ConvertDataForResource(value, spParam, spParamEx, invokeOptions);

                // add parameter
                paramValues.Add(paramName, paramValue);
            }

            // set all output value which have not been set yet
            foreach (var spParam in spInfo.Params)
            {
                if (spParam.IsOutput)
                {
                    if (string.Equals(spParam.ParamName, "Recordset", StringComparison.OrdinalIgnoreCase) || string.Equals(spParam.ParamName, "ReturnValue", StringComparison.OrdinalIgnoreCase))
                        throw new SpException($"{spInfo.ProcedureName} contains {spParam.ParamName} as a output parameter which is not supported!");

                    if (!paramValues.ContainsKey(spParam.ParamName))
                        paramValues.Add(spParam.ParamName, Undefined.Value);
                }
            }

            // execute the command
            var commandResult = await _commandProvider.Execute(spInfo, paramValues, isReadScale);

            // set ReturnValue
            if (commandResult.ReturnValue != Undefined.Value)
                spCallResults.ReturnValue = commandResult.ReturnValue;

            // fill Recordset and close dataReader BEFORE reading sqlParameters
            if (commandResult.Table != null)
                ReadRecordset(spCallResults, commandResult.Table, spInfo, invokeOptions);

            // build return params
            foreach (var outParam in commandResult.OutParams)
            {
                var outParamName = outParam.Key;
                var outParamValue = outParam.Value;
                var spParam = spInfo.Params.FirstOrDefault(x => x.ParamName.Equals($"{outParamName}", StringComparison.OrdinalIgnoreCase));
                spInfo.ExtendedProps.Params.TryGetValue(outParamName, out SpParamInfoEx spParamEx);

                // process Context
                if (outParamName.Equals("Context", StringComparison.OrdinalIgnoreCase))
                {
                    userSession.SpContext = new InvokeContext((string)outParamValue);
                    continue;
                }


                // Sign text if is need
                if (spParamEx?.SignType == SpSignMode.JwtByCertThumb)
                    outParamValue = _tokenSigner.Sign(outParamValue.ToString());

                // convert data form result
                var value = ConvertDataFromResource(invokeOptions, outParamValue);
                spCallResults.Add(outParamName, value);

                // add Alternative Calendar
                if (_alternativeCalendar.IsDateTime(spParam.SystemTypeName.ToString()))
                    spCallResults.Add(_alternativeCalendar.GetFieldName(outParamName), _alternativeCalendar.FormatDateTime(value, spParam.SystemTypeName));
            }


            userSession.SetWriteMode(isWriteMode);
            return spCallResults;
        }

        private object CheckJwt(object paramValue, SpParamInfo spParam, SpParamInfoEx spParamEx)
        {
            // Sign text if need to sign
            if (spParamEx?.SignType == SpSignMode.JwtByCertThumb && !spParam.IsOutput)
            {
                string token = paramValue.ToString();
                if (string.IsNullOrEmpty(token))
                    return string.Empty;

                if (!_tokenSigner.CheckSign(token))
                    throw new SpInvalidParamSignature(spParam.ParamName);

                // Set param value by token payload
                return StringHelper.FromBase64(token.Split('.')[1]);
            }

            return paramValue;
        }

        private bool IsReadScale(SpInfo spInfo, UserSession userSession, SpInvokeParamsInternal spi, out bool isWriteMode)
        {
            //Select ReadOnly Or Write Connection
            var dataAccessMode = spInfo.ExtendedProps != null ? spInfo.ExtendedProps.DataAccessMode : SpDataAccessMode.Write;

            //Write procedures cannot be called in ForceReadOnly anyway
            if (spi.IsForceReadOnly && dataAccessMode == SpDataAccessMode.Write)
                throw new SpMaintenanceReadOnlyException(spInfo.ProcedureName);

            //Set write request
            isWriteMode = !spi.IsForceReadOnly && dataAccessMode == SpDataAccessMode.Write;

            // Find connection string
            var isReadScale = spi.IsForceReadOnly || dataAccessMode == SpDataAccessMode.ReadSnapshot ||
                (dataAccessMode == SpDataAccessMode.Read && userSession.LastWriteTime.AddSeconds(_readonlyConnectionSyncInterval) < DateTime.Now);
            return isReadScale;
        }

        public SpInfo FindSpInfo(string spName)
        {
            if (SpInfos.TryGetValue(spName, out SpInfo spInfo))
                return spInfo;

            return null;
        }

        private object ConvertDataForResource(object value, SpParamInfo param, SpParamInfoEx paramEx, InvokeOptions invokeOptions)
        {
            //fix UserString
            if (value is string)
                value = StringHelper.FixUserString((string)value);

            if (param.SystemTypeName.ToLower() == "uniqueidentifier")
                return Guid.Parse(value as string);

            if (value is JToken || value is System.Collections.ICollection) //string is an IEnumerable
            {
                if (_useCamelCase)
                    Util.PascalizeJToken(value as JToken);

                value = JsonConvert.SerializeObject(value);
            }

            return value;
        }

        private object ConvertDataFromResource(InvokeOptions invokeOptions, object value)
        {
            // try convert json
            if (Util.IsJsonString(value as string))
            {
                try
                {
                    value = JsonConvert.DeserializeObject((string)value);
                    if (_useCamelCase)
                        Util.CamelizeJToken(value as JToken);
                }
                catch { }
            }

            return value;
        }

        private void ReadRecordset(SpCallResult spCallResult, CommandResultTable commandResultTable, SpInfo spInfo, InvokeOptions invokeOptions)
        {
            // Build return recordsetFields
            var fieldInfos = new List<FieldInfo>(commandResultTable.Fields.Length);
            for (int i = 0; i < commandResultTable.Fields.Length; i++)
            {
                fieldInfos.Add(new FieldInfo()
                {
                    Name = commandResultTable.Fields[i].Name,
                    TypeName = commandResultTable.Fields[i].TypeName,
                    ExtendedProps = spInfo.ExtendedProps.Fields.TryGetValue(commandResultTable.Fields[i].Name, out SpFieldInfo spRecodsetFiled) ? spRecodsetFiled : null
                });
            }


            // Read to Json object
            if (invokeOptions.RecordsetFormat == RecordsetFormat.Json)
                spCallResult.Recordset = ReadRecordsetAsObject(commandResultTable.Data, spInfo, fieldInfos.ToArray(), invokeOptions);

            // Read to tabSeparatedValues
            if (invokeOptions.RecordsetFormat == RecordsetFormat.TabSeparatedValues)
                spCallResult.RecordsetText = ReadRecordsetAsTabSeparatedValues(commandResultTable.Data, spInfo, fieldInfos.ToArray(), invokeOptions);
        }

        private IEnumerable<IDictionary<string, object>> ReadRecordsetAsObject(object[][] data, SpInfo spInfo, FieldInfo[] fieldInfos, InvokeOptions invokeOptions)
        {
            var recordset = new List<IDictionary<string, object>>();
            for (var i = 0; i < data.Length; i++)
            {
                var row = new Dictionary<string, object>();
                for (int j = 0; j < data[i].Length; j++)
                {
                    var fieldInfo = fieldInfos[j];
                    var value = data[i][j];

                    var itemValue = ConvertDataFromResource(invokeOptions, value);
                    row.Add(fieldInfo.Name, itemValue);

                    // Add Alternative Calendar
                    if (_alternativeCalendar.IsDateTime(fieldInfo.TypeName))
                        row.Add(_alternativeCalendar.GetFieldName(fieldInfo.Name), _alternativeCalendar.FormatDateTime(itemValue, fieldInfo.TypeName));
                }
                recordset.Add(row);
            }
            return recordset;
        }

        private string ReadRecordsetAsTabSeparatedValues(object[][] data, SpInfo spInfo, FieldInfo[] fieldInfos, InvokeOptions invokeOptions)
        {
            var stringBuilder = new StringBuilder(1 * 1000000); //1MB

            //add fields
            for (int i = 0; i < fieldInfos.Length; i++)
            {
                var fieldInfo = fieldInfos[i];

                if (i > 0)
                    stringBuilder.Append("\t");

                var fieldName = _useCamelCase ? StringHelper.ToCamelCase(fieldInfo.Name) : fieldInfo.Name;
                stringBuilder.Append(fieldName);

                //AltDateTime
                if (_alternativeCalendar.IsDateTime(fieldInfos[i].TypeName))
                    stringBuilder.Append($"\t{_alternativeCalendar.GetFieldName(fieldName)}");
            }
            stringBuilder.AppendLine();

            //add records
            var recordset = new List<IDictionary<string, object>>();
            for (var i = 0; i < data.Length; i++)
            {
                var row = new Dictionary<string, object>();
                for (int j = 0; j < data[i].Length; j++)
                {
                    var fieldInfo = fieldInfos[j];
                    var value = data[i][j];

                    // append the next line
                    if (i > 0)
                        stringBuilder.Append("\t");

                    // get value
                    var itemValue = ConvertDataFromResource(invokeOptions, value);
                    var itemValueString = itemValue?.ToString().Trim();

                    // Remove tabs
                    if (itemValue is string)
                    {
                        itemValueString = itemValueString.Replace("\"", "\"\"");
                        itemValueString = itemValueString.Replace("\t", " ");
                        itemValueString = $"\"{itemValueString}\"";

                        // Add ="" if it was a number
                        if (double.TryParse(itemValue.ToString(), out double t))
                            itemValueString = $"={itemValueString}";
                    }

                    if (itemValue is DateTime)
                        itemValueString = ((DateTime)itemValue).ToString("yyyy-MM-dd HH:mm:ss");

                    // Convert json to string
                    if (itemValue is JToken)
                        itemValueString = Util.ToJsonString(itemValue, _useCamelCase);

                    // Write the value
                    stringBuilder.Append(itemValueString);

                    // Write the AltDateTime
                    if (_alternativeCalendar.IsDateTime(fieldInfos[i].TypeName))
                        stringBuilder.Append("\t" + _alternativeCalendar.FormatDateTime(itemValue, fieldInfos[i].TypeName));
                }
                stringBuilder.AppendLine();
            }

            return stringBuilder.ToString();
        }

        private async Task<bool> CheckCaptcha(SpCall spCall, SpInvokeParamsInternal spi)
        {
            bool ret = false;

            //validate captcha
            if (spi.SpInvokeParams.InvokeOptions.CaptchaId != null || spi.SpInvokeParams.InvokeOptions.CaptchaCode != null)
            {
                await _captchaController.Verify(spi.SpInvokeParams.InvokeOptions.CaptchaId, spi.SpInvokeParams.InvokeOptions.CaptchaCode, spCall.Method);
                spi.IsCaptcha = true;
                ret = true;
            }

            return ret;
        }

        private Task<bool> UpdateRecodsetDownloadUri(SpCall spCall, SpInvokeParamsInternal spi, SpCallResult spCallResult)
        {
            bool result = false;

            var invokeOptions = spi.SpInvokeParams.InvokeOptions;
            if (invokeOptions.IsWithRecodsetDownloadUri)
            {
                //check download
                if (!_isDownloadEnabled)
                    throw new SpAccessDeniedOrObjectNotExistsException();

                var fileTitle = string.IsNullOrWhiteSpace(invokeOptions.RecordsetFileTitle) ?
                    $"{spCall.Method}-{DateTime.Now.ToString("yyyy-MM-dd HH-mm-ss")}" : invokeOptions.RecordsetFileTitle;

                var fileName = $"{fileTitle}.csv";
                var recordSetId = Util.GetRandomString(40);
                string value = null;
                if (invokeOptions.RecordsetFormat == RecordsetFormat.Json)
                {
                    value = Util.ToJsonString(spCallResult.Recordset, _useCamelCase);
                    recordSetId += ".json";
                }
                if (invokeOptions.RecordsetFormat == RecordsetFormat.TabSeparatedValues)
                {
                    value = spCallResult.RecordsetText;
                    recordSetId += ".csv";
                }

                //Cleanup
                CleanTempFolder();

                //Create file in UNC
                var filePath = Path.Combine(InvokerPath.RecordsetsFolder, recordSetId);
                File.WriteAllText(filePath, value, Encoding.Unicode);

                spCallResult.Recordset = null;
                spCallResult.RecordsetText = null;
                spCallResult.RecordsetUri = spi.SpInvokeParams.RecordsetDownloadUrlTemplate.Replace("{id}", recordSetId).Replace("{filename}", fileName);
                result = true;
            }

            return Task.FromResult(result);
        }
        private void CleanTempFolder()
        {
            // Check interval time
            var lifeTime = DateTime.Now.AddSeconds(-_downloadedRecordsetFileLifetime);
            if (LastCleanTempFolderTime != null && LastCleanTempFolderTime > lifeTime)
            {
                return; // Last cleaning was not far
            }

            //clean recordets folder
            var files = Directory.GetFiles(InvokerPath.RecordsetsFolder);
            foreach (string file in files)
            {
                FileInfo fi = new FileInfo(file);
                if (fi.LastAccessTime < lifeTime)
                {
                    fi.Delete();
                }
            }

            LastCleanTempFolderTime = DateTime.Now;
        }


        private async Task CheckDuplicateRequest(string requestId, int commandTimeout = 30)
        {
            if (string.IsNullOrEmpty(requestId))
            {
                return;
            }

            // 0 is treated as 2 hours
            if (commandTimeout == 0)
            {
                commandTimeout = 2 * 3600;
            }

            // Calculating time to life base on sp command time out
            int timeToLife = Math.Max(commandTimeout * 2, 15 * 60); //the minimum value of timeToLife is 15 min

            try
            {
                await KeyValueProvider.SetValue($"RequestId/{requestId}", "", timeToLife, false);
            }
            catch (SpObjectAlreadyExists)
            {
                throw new SpDuplicateRequestException(requestId);
            }
        }
    }
}
