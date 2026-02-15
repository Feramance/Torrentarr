import React, { useState, useEffect } from 'react';
import axios from 'axios';

function Movies() {
  const [movies, setMovies] = useState([]);
  const [loading, setLoading] = useState(true);
  const [page, setPage] = useState(1);
  const [totalPages, setTotalPages] = useState(1);
  const pageSize = 50;

  useEffect(() => {
    fetchMovies();
  }, [page]);

  const fetchMovies = async () => {
    setLoading(true);
    try {
      const response = await axios.get(`/api/movies?page=${page}&pageSize=${pageSize}`);
      setMovies(response.data.items);
      setTotalPages(response.data.totalPages);
      setLoading(false);
    } catch (err) {
      console.error('Failed to fetch movies:', err);
      setLoading(false);
    }
  };

  if (loading) {
    return <div className="loading">Loading movies...</div>;
  }

  return (
    <div className="card">
      <h2>Movies ({movies.length} of {movies.length * totalPages})</h2>
      <table className="table">
        <thead>
          <tr>
            <th>Entry ID</th>
            <th>Title</th>
            <th>Year</th>
            <th>TMDB ID</th>
            <th>Monitored</th>
          </tr>
        </thead>
        <tbody>
          {movies.map((movie) => (
            <tr key={movie.entryId}>
              <td>{movie.entryId}</td>
              <td>{movie.title}</td>
              <td>{movie.year}</td>
              <td>{movie.tmdbId}</td>
              <td>
                <span className={`badge ${movie.monitored ? 'success' : 'warning'}`}>
                  {movie.monitored ? 'Yes' : 'No'}
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

export default Movies;
