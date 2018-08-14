﻿-- Create Procedure ThrowAppException

CREATE PROCEDURE [dsp].[ThrowAppException]
	@ProcId INT = NULL, @ExceptionId INT, @Message TSTRING = NULL, @Param0 TSTRING = '<notset>', @Param1 TSTRING = '<notset>', @Param2 TSTRING = '<notset>',
	@Param3 TSTRING = '<notset>'
AS
BEGIN
	SET NOCOUNT ON;

	DECLARE @Exception TJSON;
	EXEC dsp.Exception_BuildMessage @ProcId = @ProcId, @ExceptionId = @ExceptionId, @Message = @Message, @Param0 = @Param0, @Param1 = @Param1, @Param2 = @Param2,
		@Param3 = @Param3, @Exception = @Exception OUTPUT;
	EXEC dsp.ThrowException @Exception = @Exception;

END;










