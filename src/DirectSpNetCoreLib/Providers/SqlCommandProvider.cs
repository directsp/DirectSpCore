﻿using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using DirectSp.ProcedureInfos;

namespace DirectSp.Providers
{
    class SqlCommandProvider : ICommandProvider
    {
        //sqlParameters.Add(new SqlParameter("ReturnValue", SqlDbType.Int) { Direction = ParameterDirection.ReturnValue });
        //if (spInfo.ExtendedProps.CommandTimeout != -1) sqlCommand.CommandTimeout = spInfo.ExtendedProps.CommandTimeout;

        //sqlCommand.CommandType = CommandType.StoredProcedure;
        //    sqlCommand.Parameters.AddRange(sqlParameters.ToArray());
        //    _dbLayer.OpenConnection(sqlConnection);

        //    using (var dataReader = await _dbLayer.ExecuteReaderAsync(sqlCommand))


        //set return value after closing the reader
        //var returnValueParam = sqlParameters.FirstOrDefault(x => string.Equals(x.ParameterName, "@ReturnValue", StringComparison.OrdinalIgnoreCase));
        //        if (returnValueParam != null)
        //            spCallResults.Add(returnValueParam.ParameterName.Substring(1), returnValueParam.Value != DBNull.Value? returnValueParam.Value : null);

        //ignore input parameter
        //if (outParam.Direction == ParameterDirection.Input)
        //    continue;

        //spInfo.ExtendedProps.Params.TryGetValue(outParam.ParameterName, out SpParamEx spParamEx);

        // fix null
        //if (value == DBNull.Value)
        //{
        //    return null;
        //}

        //    if (value == null)
        //return DBNull.Value;
        public Task<CommandResult> Execute(SpInfo procInfo, IDictionary<string, object> callParams, bool isReadScale)
        {
            throw new NotImplementedException();
        }

        public Task<SpInfo[]> GetSystemApi(out string context)
        {
            throw new NotImplementedException();
        }
    }
}