# syntax=docker/dockerfile:1
#
# Runtime images are pinned by digest. Dependabot updates the tag and digest together.

# Build context is the repository root (docker-compose.yml sets context: .).
FROM node:22-bookworm-slim@sha256:e9f565d32859f0cbfb52125eafe8be39390f24dac302dc3cdf566e8ba3ab21ee AS build
WORKDIR /app

# Install exactly what the lock file pins. This is the only step that needs the
# network; the runtime image makes no outbound requests.
COPY web/package.json web/package-lock.json ./
RUN npm ci --no-audit --no-fund

COPY web/ ./
RUN npm run build

FROM nginx:1.31-alpine@sha256:fed7e14be83cbd2661a75cdc5b872d936cf155d46c327084d30b8df19ae88d72

# The XSLT module is not loaded by web/nginx/nginx.conf and is the only thing
# that pulls in libxslt. Removing both drops a package the image never uses.
RUN apk del --no-network nginx-module-xslt libxslt

COPY web/nginx/nginx.conf /etc/nginx/nginx.conf
COPY web/nginx/default.conf /etc/nginx/conf.d/default.conf
COPY web/nginx/security-headers.conf /etc/nginx/snippets/security-headers.conf
COPY --chmod=755 web/nginx/40-runtime-config.sh /docker-entrypoint.d/40-runtime-config.sh

COPY --from=build /app/dist /usr/share/nginx/html

# nginx.conf and the static files stay root-owned and read-only. The runtime
# config script writes /tmp/config.js, which the tmpfs provides.
RUN chown -R nginx:nginx /usr/share/nginx/html \
    && chown nginx:nginx /etc/nginx/nginx.conf /etc/nginx/conf.d/default.conf /etc/nginx/snippets/security-headers.conf

USER nginx
EXPOSE 8080
