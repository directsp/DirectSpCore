﻿CREATE FUNCTION [dsp].[Const_MaintenanceMode_None] ()
RETURNS INT WITH SCHEMABINDING
AS
BEGIN
	RETURN 0;
END;