﻿
CREATE FUNCTION err.NoOperationId()
RETURNS INT WITH SCHEMABINDING
AS
BEGIN
	RETURN 55021;  
END
