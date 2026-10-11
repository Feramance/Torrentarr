import http from "node:http";
import fs from "node:fs";

// Isolated Arr responses for the published Host/worker smoke tests.
const server = http.createServer((request, response) => {
  response.setHeader("Content-Type", "application/json");
  const body = request.url?.includes("/queue") ? { records: [], totalRecords: 0 }
    : request.url?.includes("/system/status") ? { version: "5.0.0" }
    : request.method === "POST" ? { id: 1, status: "completed" } : [];
  response.end(JSON.stringify(body));
});
server.listen(0, "127.0.0.1", () => fs.writeFileSync(process.argv[2], String(server.address().port)));
