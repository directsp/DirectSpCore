﻿using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using DirectSp.Core.Entities;
using DirectSp.Core.Exceptions;
using System.Threading.Tasks;
using System.Text;
using Microsoft.AspNetCore.Http.Extensions;
using Newtonsoft.Json;
using System.IO;
using System;
using Microsoft.AspNetCore.Http;

namespace DirectSp.Core.Controllers
{
    public abstract class ApiController : Controller
    {
        protected abstract SpInvoker SpInvoker { get; }

        public async Task<IActionResult> Invoke(string method, [FromBody] InvokeParams invokeParams)
        {
            return await Invoke(invokeParams);
        }

        public async Task<IActionResult> Invoke([FromBody] InvokeParams invokeParams, bool isSystem = false)
        {
            try
            {
                //invoke
                SpInvokeParams spInvokeParams = new SpInvokeParams
                {
                    AuthUserId = isSystem || !User.Identity.IsAuthenticated ? null : Util.GetClaimUserId(User),
                    UserRemoteIp = HttpContext.Connection.RemoteIpAddress.ToString(),
                    InvokeOptions = invokeParams.InvokeOptions,
                    RecordsetDownloadUrlTemplate = UriHelper.BuildAbsolute(scheme: Request.Scheme, host: Request.Host, path: "/api/download/recordset") + "?id={id}&filename={filename}",
                };
                var res = await SpInvoker.Invoke(invokeParams.SpCall, spInvokeParams, isSystem);

                AddResponseHeaders();
                return JsonHelper(res);
            }
            catch (SpException ex)
            {
                AddResponseHeaders();
                return StatusCode(ex.StatusCode, ex.SpCallError);
            }
            catch (Exception err)
            {
                var ex = SpExceptionAdapter.Convert(new Exception(err.ToString())); //prevent server error for CORS
                return StatusCode(ex.StatusCode, ex.SpCallError);
            }
        }

        public async Task<IActionResult> InvokeBatch([FromBody] InvokeParamsBatch invokeParamsBatch)
        {
            try
            {
                SpInvokeParams spInvokeParams = new SpInvokeParams
                {
                    AuthUserId = Util.GetClaimUserId(User),
                    UserRemoteIp = HttpContext.Connection.RemoteIpAddress.ToString(),
                    InvokeOptions = invokeParamsBatch.InvokeOptions,
                    RecordsetDownloadUrlTemplate = UriHelper.BuildAbsolute(scheme: Request.Scheme, host: Request.Host, path: "/api/download/recordset?id={id}&filename={filename}"),
                };

                var res = await SpInvoker.Invoke(invokeParamsBatch.SpCalls, spInvokeParams);

                AddResponseHeaders();
                return JsonHelper(res);
            }
            catch (SpException ex)
            {
                AddResponseHeaders();
                return StatusCode(ex.StatusCode, ex.SpCallError);
            }
            catch (Exception err)
            {
                var ex = SpExceptionAdapter.Convert(new Exception(err.ToString())); //prevent server error for CORS
                return StatusCode(ex.StatusCode, ex.SpCallError);
            }
        }

        public async Task<IActionResult> DownloadRecordset(string id, string fileName)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(fileName))
                    fileName = "result.csv";

                AddResponseHeaders();

                //get file
                var filePath = Path.Combine(SpInvoker.InvokerPath.RecordsetsFolder, id);
                var fs = System.IO.File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);

                return File(fs, "text/csv", fileName);
            }
            catch (SpAccessDeniedOrObjectNotExistsException)
            {
                AddResponseHeaders();
                return new NotFoundResult();
            }
            catch (SpException ex)
            {
                AddResponseHeaders();
                return StatusCode(ex.StatusCode, ex.SpCallError);
            }
            catch (Exception err)
            {
                var ex = SpExceptionAdapter.Convert(new Exception(err.ToString())); //prevent server error for CORS
                return StatusCode(ex.StatusCode, ex.SpCallError);
            }
        }
        private JsonResult JsonHelper(object data)
        {
            var serializerSettings = new JsonSerializerSettings();
            if (SpInvoker.Options.UseCamelCase)
                serializerSettings.ContractResolver = new Newtonsoft.Json.Serialization.CamelCasePropertyNamesContractResolver();

            return base.Json(data, serializerSettings);
        }

        private void AddResponseHeaders()
        {
            //set app version
            Request.HttpContext.Response.Headers.Add("DSP-AppVersion", SpInvoker.AppVersion);
        }

    }
}
