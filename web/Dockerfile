# syntax=docker/dockerfile:1

# Build context is the repository root (docker-compose.yml sets context: .).
FROM node:22-bookworm-slim AS build
WORKDIR /app

# Install exactly what the lock file pins. This is the only step that needs the
# network; the runtime image makes no outbound requests.
COPY web/package.json web/package-lock.json ./
RUN npm ci --no-audit --no-fund

COPY web/ ./
RUN npm run build

FROM nginx:1.27-alpine

COPY web/nginx/default.conf /etc/nginx/conf.d/default.conf
COPY web/nginx/40-runtime-config.sh /docker-entrypoint.d/40-runtime-config.sh
RUN chmod +x /docker-entrypoint.d/40-runtime-config.sh

COPY --from=build /app/dist /usr/share/nginx/html

EXPOSE 80
