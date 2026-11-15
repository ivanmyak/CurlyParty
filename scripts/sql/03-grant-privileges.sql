-- Привилегии роли приложения внутри базы curlypartydb.
-- ВАЖНО: подключаться уже к базе curlypartydb (в Adminer выберите её в списке баз).
GRANT ALL PRIVILEGES ON DATABASE curlypartydb TO curlyparty;
ALTER SCHEMA public OWNER TO curlyparty;
GRANT ALL ON SCHEMA public TO curlyparty;
ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT ALL ON TABLES TO curlyparty;
ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT ALL ON SEQUENCES TO curlyparty;
