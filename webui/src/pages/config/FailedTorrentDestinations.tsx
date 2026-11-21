import { useEffect, useState } from "react";
import { getFailedTorrentDestinations } from "../../api/client";

interface Destination {
  Client: string;
  Category: string;
}

export function FailedTorrentDestinations({
  category,
  value,
  onChange,
}: {
  category: string;
  value: Destination[];
  onChange: (value: Destination[]) => void;
}) {
  const [clients, setClients] =
    useState<Awaited<ReturnType<typeof getFailedTorrentDestinations>>>();
  const [failed, setFailed] = useState(false);

  useEffect(() => {
    let current = true;
    setClients(undefined);
    setFailed(false);
    void getFailedTorrentDestinations(category).then(
      (result) => {
        if (current) setClients(result);
      },
      () => {
        if (current) setFailed(true);
      },
    );
    return () => {
      current = false;
    };
  }, [category]);

  if (failed)
    return <p role="alert">Destination categories are unavailable.</p>;
  if (!clients) return <p role="status">Loading destination categories…</p>;
  if (!clients.length) return <p>No configured qBittorrent clients.</p>;
  return (
    <div className="field field--full-width">
      {clients.map(({ client, available, categories }) => {
        const selected =
          value.find((row) => row.Client.toLowerCase() === client.toLowerCase())
            ?.Category ?? "";
        return (
          <label key={client} className="field">
            Destination on {client}
            <select
              value={selected}
              disabled={!available}
              onChange={(event) => {
                const remaining = value.filter(
                  (row) => row.Client.toLowerCase() !== client.toLowerCase(),
                );
                const choice = event.target.value;
                onChange(
                  choice
                    ? [...remaining, { Client: client, Category: choice }]
                    : remaining,
                );
              }}
            >
              <option value="">
                {available
                  ? "Select a category"
                  : "Client categories unavailable"}
              </option>
              {selected &&
                !categories.some((option) => option.name === selected) && (
                  <option value={selected} disabled>
                    {selected} (unavailable)
                  </option>
                )}
              {categories.map((option) => (
                <option
                  key={option.name}
                  value={option.name}
                  disabled={!!option.unavailableReason}
                >
                  {option.name}
                  {option.arrOwner
                    ? ` — Arr managed by ${option.arrOwner}`
                    : option.unavailableReason
                      ? " — unavailable"
                      : ""}
                </option>
              ))}
            </select>
            {
              categories.find((option) => option.name === selected)
                ?.unavailableReason
            }
          </label>
        );
      })}
    </div>
  );
}
