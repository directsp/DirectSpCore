﻿
CREATE FUNCTION [dsp].[Context_UserId] (@Context TCONTEXT)
RETURNS TSTRING
AS
BEGIN
    RETURN JSON_VALUE(@Context, '$.UserId');
END;



