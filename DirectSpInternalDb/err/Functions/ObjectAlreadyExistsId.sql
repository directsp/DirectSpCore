﻿
CREATE FUNCTION err.ObjectAlreadyExistsId()
RETURNS INT WITH SCHEMABINDING
AS
BEGIN
	RETURN 55004;  
END
