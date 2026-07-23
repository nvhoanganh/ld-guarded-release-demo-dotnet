# Beacon (Phase 2 release orchestrator) — deployable image for Render.
#
# Beacon lives in the launchdarkly-auto-factory monorepo and depends on the
# @auto-factory/shared workspace, so it must be built from the repo root. This
# Dockerfile clones that repo at a PINNED commit and builds the beacon package —
# nothing from THIS repo's build context is needed (the clone is self-contained).
#
# Bump AUTOFACTORY_SHA to move Beacon forward; keep it pinned so a Render deploy
# is reproducible.
#
#   docker build -t auto-factory-beacon ./factory
#   docker run -p 8080:8080 --env-file beacon.env auto-factory-beacon

FROM node:22-slim AS build
ARG AUTOFACTORY_REPO=https://github.com/nvhoanganh/launchdarkly-auto-factory.git
ARG AUTOFACTORY_SHA=e4261ccbe377bcb2c1f40e43d748162143bd0ce4
RUN apt-get update && apt-get install -y --no-install-recommends git \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
RUN git clone "$AUTOFACTORY_REPO" . && git checkout "$AUTOFACTORY_SHA"
RUN npm ci && npm run build && npm prune --omit=dev

FROM node:22-slim
WORKDIR /app
ENV NODE_ENV=production
# Render injects PORT; Beacon binds process.env.PORT (falls back to 8080).
COPY --from=build /app/node_modules ./node_modules
COPY --from=build /app/package.json ./package.json
COPY --from=build /app/packages ./packages
COPY --from=build /app/config ./config
EXPOSE 8080
CMD ["node", "packages/beacon/dist/server.js"]
