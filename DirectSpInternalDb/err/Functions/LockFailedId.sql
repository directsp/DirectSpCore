﻿
CREATE FUNCTION err.LockFailedId()
RETURNS INT WITH SCHEMABINDING
AS
BEGIN
	RETURN 55013;  
END