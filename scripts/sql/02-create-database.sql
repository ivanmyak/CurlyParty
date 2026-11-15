-- Отдельная база данных CurlyParty.
-- CREATE DATABASE нельзя выполнять внутри DO-блока или транзакции,
-- поэтому скрипт состоит из одной команды. Владелец — роль приложения.
-- В Adminer: сервер postgresdb, база postgres, пользователь-администратор.
CREATE DATABASE curlypartydb OWNER curlyparty;
