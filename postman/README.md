# Postman Collection

Import `Telemetry Simulator.postman_collection.json` into Postman (Import -> Files) to hit this service's API locally.

## Requests

- **Upload Files (42)** — uploads a raw flight log (`rawFile`) and its mapping config (`mappingFile`) for a tail number. Must run before Start Simulation for that same tail number, or the simulator returns `UploadNotFound`.
- **Start Simulation** — starts replaying the uploaded log as UDP packets to `Host:Port` at the given interval. `Host` is resolved server-side via DNS, so a docker-network container name (e.g. `telemetry-device`) works even if the container is recreated with a new IP.
- **Stop Simulation** — stops an in-progress simulation for a tail number.

## Typical order

1. Upload Files
2. Start Simulation
3. Stop Simulation when done
