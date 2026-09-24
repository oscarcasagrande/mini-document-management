import type { NextConfig } from "next";

const nextConfig: NextConfig = {
  // The container runs the standalone server, so only the files it needs are copied into the image.
  output: "standalone",
  reactStrictMode: true,
  poweredByHeader: false,
  // The BFF is the only path from the browser to the API, so the browser never learns the internal
  // address of the API container.
  async headers() {
    return [
      {
        source: "/(.*)",
        headers: [
          { key: "X-Content-Type-Options", value: "nosniff" },
          { key: "Referrer-Policy", value: "no-referrer" },
          { key: "X-Frame-Options", value: "SAMEORIGIN" },
        ],
      },
    ];
  },
};

export default nextConfig;
