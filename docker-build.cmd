docker build -f .\testapi\Dockerfile -t testapi:dev .
docker rm -f testapi-dev 2>nul
docker run -d --name testapi-dev -p 8080:80 -m 500m --memory-reservation 50m --cpus=0.5 testapi:de