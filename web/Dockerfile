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
COPY web/nginx/security-headers.conf /etc/nginx/snippets/security-headers.conf
# The server block is a template: 30-upstream-config.sh renders it into
# /tmp/conf.d at container start with the upstreams and resolver for the target
# environment. It stays root-owned and read-only; the script only reads it.
COPY web/nginx/default.conf.template /etc/nginx/templates/default.conf.template
COPY --chmod=755 web/nginx/30-upstream-config.sh /docker-entrypoint.d/30-upstream-config.sh
COPY --chmod=755 web/nginx/40-runtime-config.sh /docker-entrypoint.d/40-runtime-config.sh

COPY --from=build /app/dist /usr/share/nginx/html

# Remove the stock server block (port 80) and the stock envsubst template
# renderer. nginx.conf includes only /tmp/conf.d, and 20-envsubst-on-templates.sh
# would otherwise run envsubst over our template (expanding $host,
# $request_uri, ...) and write into the read-only /etc/nginx/conf.d.
RUN rm -f /etc/nginx/conf.d/default.conf /docker-entrypoint.d/20-envsubst-on-templates.sh \
    && chmod 0644 /etc/nginx/templates/default.conf.template \
    && chown -R nginx:nginx /usr/share/nginx/html \
    && chown nginx:nginx /etc/nginx/nginx.conf /etc/nginx/snippets/security-headers.conf

USER nginx
EXPOSE 8080
