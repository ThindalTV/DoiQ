FROM alpine:3.21
RUN apk add --no-cache sqlite tzdata
COPY backup.sh backup-loop.sh /usr/local/bin/
RUN chmod +x /usr/local/bin/backup.sh /usr/local/bin/backup-loop.sh
ENTRYPOINT ["/usr/local/bin/backup-loop.sh"]
