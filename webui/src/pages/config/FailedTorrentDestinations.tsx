import { useEffect, useId, useState } from "react";
import { getFailedTorrentDestinations } from "../../api/client";

interface Destination {
  [key: string]: unknown;
  Client: string;
  Category: string;
}

export function FailedTorrentDestinations({
  category,
  dataPath,
  value,
  onChange,
}: {
  category: string;
  dataPath?: string;
  value: Destination[];
  onChange: (value: Destination[]) => void;
}) {
  const listName = useId();
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
    <section
      className="field field--full-width"
      data-field-path={dataPath}
      aria-label="Failed torrent destinations"
    >
      <strong>Failed torrent destinations</strong>
      {clients.map(({ client, available, categories }) => {
        const index = value.findIndex(
          (row) => row.Client.toLowerCase() === client.toLowerCase(),
        );
        const selected = value[index]?.Category ?? "";
        return (
          <details
            key={client}
            name={listName}
            className="collection-row"
            data-field-path={
              index >= 0 && dataPath
                ? `${dataPath}.${index}.Category`
                : undefined
            }
          >
            <summary>
              {client} → {selected || "No destination"}
            </summary>
            <label className="field collection-row__body">
              Destination on {client}
              <select
                value={selected}
                disabled={!available}
                onChange={(event) => {
                  const choice = event.target.value;
                  if (index === -1) {
                    if (choice)
                      onChange([
                        ...value,
                        { Client: client, Category: choice },
                      ]);
                  } else {
                    onChange(
                      choice
                        ? value.map((row, i) =>
                            i === index ? { ...row, Category: choice } : row,
                          )
                        : value.filter((_, i) => i !== index),
                    );
                  }
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
          </details>
        );
      })}
    </section>
  );
}
