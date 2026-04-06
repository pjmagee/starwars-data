FROM mongo:latest
COPY . /migrations
ENTRYPOINT ["/bin/sh"]
CMD ["-c", "mongosh \"$MDB_MCP_CONNECTION_STRING\" --quiet --eval \"STARWARS_DB='$STARWARS_DB'\" --file /migrations/migrate.js"]
