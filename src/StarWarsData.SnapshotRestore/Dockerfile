# Run-once developer-onboarding snapshot restore.
# mongo:latest ships mongosh (used for the idempotency probe) but NOT
# mongorestore since 4.4 — add the database tools + curl. The mongo image
# already has the MongoDB apt repo configured.
FROM mongo:latest
RUN apt-get update \
 && apt-get install -y --no-install-recommends curl mongodb-database-tools ca-certificates \
 && rm -rf /var/lib/apt/lists/*
COPY restore.sh /restore.sh
RUN chmod +x /restore.sh
ENTRYPOINT ["/bin/sh", "-c", "/restore.sh"]
