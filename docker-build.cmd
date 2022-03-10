docker build -f .\testapi\Dockerfile -t testapi:dev .
docker run --rm -d -p 80:80 -m 500m --memory-reservation 50m --cpus=0.5 testapi:dev