﻿using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using DirectSp.Core.Entities;
using DirectSp.Core.Exceptions;
using System.Threading.Tasks;
using System.Text;
using Microsoft.AspNetCore.Http.Extensions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

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
                    UserId = isSystem || !User.Identity.IsAuthenticated ? null : Util.GetClaimUserId(User),
                    UserRemoteIp = HttpContext.Connection.RemoteIpAddress.ToString(),
                    InvokeOptions = invokeParams.InvokeOptions,
                    RecordsetDownloadUrlTemplate = UriHelper.BuildAbsolute(scheme: Request.Scheme, host: Request.Host, path: "/api/download/recordset") + "?id={id}&filename={filename}",
                };
                var res = await SpInvoker.Invoke(invokeParams.SpCall, spInvokeParams, isSystem);
                return Json(res, invokeParams.InvokeOptions.IsAntiXss);
            }
            catch (SpException ex)
            {
                return StatusCode(ex.StatusCode, ex.SpCallError);
            }

        }

        public async Task<IActionResult> InvokeBatch([FromBody] InvokeParamsBatch invokeParamsBatch)
        {
            try
            {
                SpInvokeParams spInvokeParams = new SpInvokeParams
                {
                    UserId = Util.GetClaimUserId(User),
                    UserRemoteIp = HttpContext.Connection.RemoteIpAddress.ToString(),
                    InvokeOptions = invokeParamsBatch.InvokeOptions,
                    RecordsetDownloadUrlTemplate = UriHelper.BuildAbsolute(scheme: Request.Scheme, host: Request.Host, path: "/api/download/recordset?id={id}&filename={filename}"),
                };

                var res = await SpInvoker.Invoke(invokeParamsBatch.SpCalls, spInvokeParams);
                return Json(res, invokeParamsBatch.InvokeOptions.IsAntiXss);
            }
            catch (SpException ex)
            {
                return StatusCode(ex.StatusCode, ex.SpCallError);
            }
        }

        public async Task<IActionResult> DownloadRecordset(string id, string fileName)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(fileName))
                    fileName = "result.csv";

                //get file
                var stream = await SpInvoker.KeyValue.GetTextStream($"recordset/{id}", Encoding.Unicode);
                return File(stream, "text/csv", fileName);
            }
            catch (SpAccessDeniedOrObjectNotExistsException)
            {
                return new NotFoundResult();
            }
            catch (SpException ex)
            {
                return StatusCode(ex.StatusCode, ex.SpCallError);
            }
        }
        private JsonResult Json(object data, bool IsAntiXss)
        {
            var serializerSettings = new JsonSerializerSettings();
            if (SpInvoker.Options.UseCamelCase)
                serializerSettings.ContractResolver = new Newtonsoft.Json.Serialization.CamelCasePropertyNamesContractResolver();

            if (IsAntiXss)
                serializerSettings.Converters.Add( new AntiXssConverter());

            return base.Json(data, serializerSettings);
        }

    }
}