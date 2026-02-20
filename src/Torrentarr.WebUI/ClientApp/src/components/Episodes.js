import React, { useState, useEffect } from 'react';
import axios from 'axios';

function Episodes() {
  const [episodes, setEpisodes] = useState([]);
  const [loading, setLoading] = useState(true);
  const [page, setPage] = useState(1);
  const [totalPages, setTotalPages] = useState(1);
  const pageSize = 50;

  useEffect(() => {
    fetchEpisodes();
  }, [page]);

  const fetchEpisodes = async () => {
    setLoading(true);
    try {
      const response = await axios.get(`/api/episodes?page=${page}&pageSize=${pageSize}`);
      setEpisodes(response.data.items);
      setTotalPages(response.data.totalPages);
      setLoading(false);
    } catch (err) {
      console.error('Failed to fetch episodes:', err);
      setLoading(false);
    }
  };

  if (loading) {
    return <div className="loading">Loading episodes...</div>;
  }

  return (
    <div className="card">
      <h2>Episodes ({episodes.length} of {episodes.length * totalPages})</h2>
      <table className="table">
        <thead>
          <tr>
            <th>Entry ID</th>
            <th>Series</th>
            <th>Season</th>
            <th>Episode</th>
            <th>Series ID</th>
            <th>Monitored</th>
          </tr>
        </thead>
        <tbody>
          {episodes.map((episode) => (
            <tr key={episode.entryId}>
              <td>{episode.entryId}</td>
              <td>{episode.seriesTitle}</td>
              <td>{episode.seasonNumber}</td>
              <td>{episode.episodeNumber}</td>
              <td>{episode.seriesId}</td>
              <td>
                <span className={`badge ${episode.monitored ? 'success' : 'warning'}`}>
                  {episode.monitored ? 'Yes' : 'No'}
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

export default Episodes;
