-- Роль приложения CurlyParty.
-- Выполнить один раз от имени администратора PostgreSQL
-- (например, в Adminer: сервер postgresdb, база postgres, пользователь SuperAdmiral).
-- Пароль должен совпадать с CURLYPARTY_DB_PASSWORD в дебаговом оркестре.
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'curlyparty') THEN
        CREATE ROLE curlyparty LOGIN PASSWORD 'curlyparty_dev';
    ELSE
        RAISE NOTICE 'Роль curlyparty уже существует';
    END IF;
END
$$;
