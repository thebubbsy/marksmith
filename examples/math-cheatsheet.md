# Linear Algebra Cheatsheet

A one-page refresher rendered with KaTeX.

## Vectors & norms

The Euclidean norm of a vector $\mathbf{x} \in \mathbb{R}^n$ is $\|\mathbf{x}\|_2 = \sqrt{\sum_i x_i^2}$,
and the dot product is $\mathbf{x}^\top \mathbf{y} = \sum_i x_i y_i$.

## Matrix identities

$$
(AB)^\top = B^\top A^\top
\qquad
(A^{-1})^\top = (A^\top)^{-1}
$$

For a square matrix $A$, the eigenvalue equation is:

$$ A\mathbf{v} = \lambda \mathbf{v}, \qquad \mathbf{v} \neq \mathbf{0} $$

## Useful decompositions

| Name | Form | Notes |
| --- | --- | --- |
| Eigen | $A = Q \Lambda Q^{-1}$ | square, diagonalizable |
| SVD | $A = U \Sigma V^\top$ | any real matrix |
| Cholesky | $A = L L^\top$ | symmetric positive-definite |

## Gaussian density

$$
p(\mathbf{x}) = \frac{1}{(2\pi)^{n/2}\,|\Sigma|^{1/2}}
\exp\!\left(-\tfrac{1}{2}(\mathbf{x}-\boldsymbol\mu)^\top \Sigma^{-1} (\mathbf{x}-\boldsymbol\mu)\right)
$$
