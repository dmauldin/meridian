# Sourced by the db scripts: gives a database server to work in, as $SERVER_URL (a libpq
# connection string for its "postgres" database) and $SERVER_DIR/$SERVER_PORT for local use.
#
# With DATABASE_URL set (CI's Postgres service), uses that server. Otherwise starts a throwaway
# local cluster from the Postgres binaries on this machine, stopped and deleted on exit.
# Callers create their own databases on it and drop them in their own EXIT trap; call
# scratch_postgres_stop from it last.
if [[ -n "${DATABASE_URL:-}" ]]; then
  SERVER_URL="$DATABASE_URL"
  scratch_postgres_stop() { :; }
  # postgres://u:p@host:port/db -> the same server, a given database
  scratch_url_for() { echo "${DATABASE_URL%/*}/$1"; }
else
  _BIN="$(ls -d /usr/lib/postgresql/*/bin 2>/dev/null | sort -V | tail -1)"
  [[ -x "$_BIN/initdb" ]] || { echo "no Postgres binaries found; set DATABASE_URL"; exit 1; }
  SERVER_DIR="$(mktemp -d)"
  _RUN=()
  if [[ "$(id -u)" == 0 ]]; then chown postgres "$SERVER_DIR"; _RUN=(runuser -u postgres --); fi
  SERVER_PORT=$((20000 + $$ % 10000))
  "${_RUN[@]}" "$_BIN/initdb" -D "$SERVER_DIR" -U postgres -A trust >/dev/null
  "${_RUN[@]}" "$_BIN/pg_ctl" -D "$SERVER_DIR" -o "-p $SERVER_PORT -k $SERVER_DIR -c listen_addresses=''" -w start >/dev/null
  scratch_postgres_stop() { "${_RUN[@]}" "$_BIN/pg_ctl" -D "$SERVER_DIR" -m immediate stop >/dev/null; rm -rf "$SERVER_DIR"; }
  SERVER_URL="host=$SERVER_DIR port=$SERVER_PORT user=postgres dbname=postgres"
  scratch_url_for() { echo "host=$SERVER_DIR port=$SERVER_PORT user=postgres dbname=$1"; }
fi
