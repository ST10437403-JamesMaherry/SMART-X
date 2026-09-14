# syntax=docker/dockerfile:1

# =============================================================================
# BUILD STAGE
# =============================================================================
#
# Node.js is required only to compile the React + TypeScript application.
# Development dependencies and source files are not carried into the final
# Nginx image.
#
FROM node:24-alpine AS build

WORKDIR /app

# Install dependencies from the lockfile before copying the remaining source.
# This maximises Docker layer reuse and guarantees a reproducible npm install.
COPY src/SmartX.Client/package.json ./
COPY src/SmartX.Client/package-lock.json ./

RUN npm ci

COPY src/SmartX.Client/ ./

# Most production Smart-X deployments use same-origin API routing through
# Nginx, represented by an empty value.
#
# The argument remains available for deployments that intentionally expose the
# API from a separate origin.
ARG VITE_API_BASE_URL=""

ENV VITE_API_BASE_URL=${VITE_API_BASE_URL}

RUN npm run build


# =============================================================================
# RUNTIME STAGE
# =============================================================================
#
# nginx-unprivileged is intentionally used instead of the normal root-based
# Nginx image. The service therefore listens on unprivileged port 8080 and
# does not require root at runtime.
#
FROM nginxinc/nginx-unprivileged:alpine AS final

COPY src/SmartX.Client/nginx.conf /etc/nginx/conf.d/default.conf

COPY --from=build /app/dist /usr/share/nginx/html

EXPOSE 8080

HEALTHCHECK --interval=10s --timeout=3s --start-period=5s --retries=5 \
    CMD wget --quiet --tries=1 --spider http://127.0.0.1:8080/healthz || exit 1
