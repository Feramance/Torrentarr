import React, { useState, useEffect } from 'react';
import axios from 'axios';

function Torrents() {
  const [torrents, setTorrents] = useState([]);
  const [loading, setLoading] = useState(true);
  const [page, setPage] = useState(1);
  const [totalPages, setTotalPages] = useState(1);
  const pageSize = 50;

  useEffect(() => {
    fetchTorrents();
    const interval = setInterval(fetchTorrents, 5000); // Poll every 5 seconds
    return () => clearInterval(interval);
  }, [page]);

  const fetchTorrents = async () => {
    setLoading(true);
    try {
      const response = await axios.get(`/api/torrents?page=${page}&pageSize=${pageSize}`);
      setTorrents(response.data.items);
      setTotalPages(response.data.totalPages);
      setLoading(false);
    } catch (err) {
      console.error('Failed to fetch torrents:', err);
      setLoading(false);
    }
  };

  if (loading && torrents.length === 0) {
    return <div className="loading">Loading torrents...</div>;
  }

  return (
    <div className="card">
      <h2>Torrents ({torrents.length} of {torrents.length * totalPages})</h2>
      <table className="table">
        <thead>
          <tr>
            <th>Hash</th>
            <th>Category</th>
            <th>Instance</th>
            <th>Imported</th>
          </tr>
        </thead>
        <tbody>
          {torrents.map((torrent) => (
            <tr key={torrent.hash}>
              <td>
                <code style={{ fontSize: '12px' }}>
                  {torrent.hash.substring(0, 12)}...
                </code>
              </td>
              <td>{torrent.category}</td>
              <td>{torrent.qbitInstance}</td>
              <td>
                <span className={`badge ${torrent.imported ? 'success' : 'info'}`}>
                  {torrent.imported ? 'Imported' : 'Processing'}
                </span>
              </td>
            </tr>
          ))}
        </tbody>
      </table>

      {totalPages > 1 && (
        <div className="pagination">
          <button
            onClick={() => setPage(p => Math.max(1, p - 1))}
            disabled={page === 1}
          >
            Previous
          </button>
          <span>Page {page} of {totalPages}</span>
          <button
            onClick={() => setPage(p => Math.min(totalPages, p + 1))}
            disabled={page === totalPages}
          >
            Next
          </button>
        </div>
      )}
    </div>
  );
}

export default Torrents;
