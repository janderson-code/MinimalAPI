﻿using System.ComponentModel.DataAnnotations;

namespace Api.Models.Todo;

public class TodoItem
{
    public int Id { get; set; }

    [Required] public string? Title { get; set; }

    public bool IsCompleted { get; set; }
    public User User { get; set; } = null!;
    public int UserId { get; set; }
    public DateTime CreatedOn { get; set; }
}