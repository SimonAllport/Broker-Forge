# Orders, Inventory and Tickets mock for the dev environment. Build context: tools/mock-api.
FROM node:22-alpine
WORKDIR /app
COPY package.json data.js inventory.js tickets.js server.js ./
ENV PORT=8080
EXPOSE 8080
USER node
CMD ["node", "server.js"]
